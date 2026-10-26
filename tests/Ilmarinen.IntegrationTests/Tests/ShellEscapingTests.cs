using Ilmarinen.Docker;
using Ilmarinen.Models;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// Nested-container commands travel as a single string through the host-side "/bin/sh -c" in
/// <see cref="DockerJobContext"/>, so every argument must be quoted against that outer shell.
/// These tests evaluate the produced strings through a real /bin/sh and assert argv fidelity —
/// the historical failure was $? in a nested exit-code-preserving wrapper being expanded by the
/// outer shell, silently turning failed builds green.
/// </summary>
[TestFixture]
[Category("Integration")]
public class ShellEscapingTests
{
    private static readonly string[] HostileArgs =
    [
        "simple",
        "with space",
        "ec=$?; dotnet build-server shutdown || true; exit $ec",
        "`whoami`",
        "$(whoami)",
        "a;b",
        "a&&b",
        "a|b",
        "a>b",
        "*",
        "~",
        "don't",
        "say \"hi\"",
        "back\\slash",
        "line1\nline2",
        "tab\there",
        "",
    ];

    [Test]
    public async Task EscapeShellArg_PreservesEveryArgumentThroughTheShell()
    {
        var escaped = string.Join(" ", HostileArgs.Select(DockerJobContext.EscapeShellArg));
        var (exitCode, stdout, stderr) = await RunShAsync($"dump() {{ for a in \"$@\"; do printf '%s\\0' \"$a\"; done; }}; dump {escaped}");

        Assert.That(exitCode, Is.EqualTo(0), stderr);
        Assert.That(SplitNulTerminated(stdout), Is.EqualTo(HostileArgs));
    }

    [Test]
    public async Task BuildDockerRunCommand_PreservesTheNestedCommandVerbatim()
    {
        var context = new DockerJobContext(null!, "container", "/workspace", "/localwork", "/hostwork", "testnet", "main", "abc123", _ => null);
        const string innerScript = "false; ec=$?; true; exit $ec";
        var cmdStr = context.BuildDockerRunCommand(ImageRef.From("test-image"), ["sh", "-c", innerScript]);

        var argv = await ArgvThroughShellAsync(cmdStr, "docker");

        Assert.That(argv[0], Is.EqualTo("run"));
        Assert.That(argv, Does.Contain("/hostwork:/workspace"),
            "the bind mount must use the daemon-side host path, not the process-local path");
        Assert.That(argv[^3..], Is.EqualTo(new[] { "sh", "-c", innerScript }),
            "the nested command must reach docker exactly as written — no outer-shell expansion");
    }

    /// <summary>The workspace's host path is wherever the worker keeps it, which the shell has no business interpreting. A colon is left out: docker splits the bind spec on it whatever the quoting.</summary>
    [Test]
    public async Task BuildDockerRunCommand_HostilePathAndNetwork_ReachDockerIntact()
    {
        const string hostWorkDir = "/host work/$HOME/it's";
        var context = new DockerJobContext(null!, "container", "/workspace", "/localwork", hostWorkDir, "test net", "main", "abc123", _ => null);

        var argv = await ArgvThroughShellAsync(context.BuildDockerRunCommand(ImageRef.From("test-image"), ["true"]), "docker");

        Assert.That(ValueAfter(argv, "-v"), Is.EqualTo($"{hostWorkDir}:/workspace"));
        Assert.That(ValueAfter(argv, "--network"), Is.EqualTo("test net"));
    }

    [Test]
    public async Task BuildDockerBuildCommand_HostileTag_ReachesDockerIntact()
    {
        const string tag = "repo/img:v1;echo pwned $(id)";

        var argv = await ArgvThroughShellAsync(DockerJobContext.BuildDockerBuildCommand("Dockerfile", tag, ".", null), "docker");

        Assert.That(ValueAfter(argv, "-t"), Is.EqualTo(tag));
        Assert.That(argv[^1], Is.EqualTo("."));
    }

    [Test]
    public async Task BuildDockerServiceCommand_HostileNameAndImage_ReachDockerIntact()
    {
        var context = new DockerJobContext(null!, "container", "/workspace", "/localwork", "/hostwork", "testnet", "main", "abc123", _ => null);

        var argv = await ArgvThroughShellAsync(context.BuildDockerServiceCommand(ImageRef.From("img;echo pwned"), "testnet-my svc", [5432]), "docker");

        Assert.That(ValueAfter(argv, "--name"), Is.EqualTo("testnet-my svc"));
        Assert.That(argv[^1], Is.EqualTo("img;echo pwned"));
    }

    /// <summary>A query string's &amp; would otherwise background the check and report healthy on the spot.</summary>
    [Test]
    public async Task BuildHealthCheckCommand_UrlWithQueryString_ReachesCurlIntact()
    {
        const string url = "http://svc:8080/health?a=1&b=2";

        var argv = await ArgvThroughShellAsync(DockerJobContext.BuildHealthCheckCommand(url), "curl");

        Assert.That(argv, Is.EqualTo(new[] { "-sf", url }));
    }

    [Test]
    public async Task BuildHealthCheckCommand_TcpUrl_SplitsHostAndPortForNc()
    {
        var argv = await ArgvThroughShellAsync(DockerJobContext.BuildHealthCheckCommand("tcp://db:5432"), "nc");

        Assert.That(argv, Is.EqualTo(new[] { "-z", "db", "5432" }));
    }

    [Test]
    public async Task BuildAgentBuildCommand_Success_PrintsOnlyTheTagOnStdout()
    {
        var (exitCode, stdout, stderr) = await RunWithStubAsync(DockerJobContext.BuildAgentBuildCommand("Dockerfile", "repo/img:v1", ".", null), "docker", "printf 'build progress\\n'");

        Assert.That(exitCode, Is.EqualTo(0), stderr);
        Assert.That(stdout, Is.EqualTo("repo/img:v1\n"));
        Assert.That(stderr, Does.Contain("build progress"), "docker's own output still reaches the caller, on stderr");
    }

    [Test]
    public async Task BuildAgentBuildCommand_DockerFails_ExitsWithItsCodeAndNoTag()
    {
        var (exitCode, stdout, _) = await RunWithStubAsync(DockerJobContext.BuildAgentBuildCommand("Dockerfile", "repo/img:v1", ".", null), "docker", "exit 3");

        Assert.That(exitCode, Is.EqualTo(3));
        Assert.That(stdout, Is.Empty);
    }

    private static string ValueAfter(string[] argv, string flag)
    {
        var index = Array.IndexOf(argv, flag);
        Assert.That(index, Is.GreaterThanOrEqualTo(0), $"{flag} missing from {string.Join(" | ", argv)}");
        return argv[index + 1];
    }

    /// <summary>
    /// Evaluates <paramref name="command"/> through a real /bin/sh with a stub <paramref name="program"/> on PATH that dumps its argv NUL-terminated, so we see exactly what the shell hands it.
    /// </summary>
    private static async Task<string[]> ArgvThroughShellAsync(string command, string program)
    {
        var (exitCode, stdout, stderr) = await RunWithStubAsync(command, program, "for a in \"$@\"; do printf '%s\\0' \"$a\"; done");

        Assert.That(exitCode, Is.EqualTo(0), stderr);
        return SplitNulTerminated(stdout);
    }

    /// <summary>Evaluates <paramref name="command"/> through a real /bin/sh with a stub <paramref name="program"/> on PATH whose body is <paramref name="stubBody"/>.</summary>
    private static async Task<(int exitCode, string stdout, string stderr)> RunWithStubAsync(string command, string program, string stubBody)
    {
        var stubDir = Path.Combine(Path.GetTempPath(), $"ilmarinen-stub-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stubDir);
        try
        {
            var stubPath = Path.Combine(stubDir, program);
            await File.WriteAllTextAsync(stubPath, $"#!/bin/sh\n{stubBody}\n");
            File.SetUnixFileMode(stubPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            var env = new Dictionary<string, string> { ["PATH"] = stubDir + ":" + Environment.GetEnvironmentVariable("PATH") };
            return await RunShAsync(command, env);
        }
        finally
        {
            Directory.Delete(stubDir, true);
        }
    }

    private static string[] SplitNulTerminated(string s)
    {
        var parts = s.Split('\0');
        Assert.That(parts[^1], Is.EqualTo(""), "output must end with a NUL terminator");
        return parts[..^1];
    }

    private static async Task<(int exitCode, string stdout, string stderr)> RunShAsync(string script, IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        if (env != null)
        {
            foreach (var (key, value) in env)
            {
                psi.Environment[key] = value;
            }
        }

        using var process = Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
