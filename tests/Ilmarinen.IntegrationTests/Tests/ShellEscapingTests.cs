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
        var context = new DockerJobContext(null!, "container", "/workspace", "/hostwork", "testnet", "main", "abc123", _ => null);
        const string innerScript = "false; ec=$?; true; exit $ec";
        var cmdStr = context.BuildDockerRunCommand(ImageRef.From("test-image"), ["sh", "-c", innerScript]);

        // A stub `docker` on PATH dumps its argv NUL-terminated, so we see exactly what the outer shell hands it.
        var stubDir = Path.Combine(Path.GetTempPath(), $"ilmarinen-docker-stub-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stubDir);
        try
        {
            var stubPath = Path.Combine(stubDir, "docker");
            await File.WriteAllTextAsync(stubPath, "#!/bin/sh\nfor a in \"$@\"; do printf '%s\\0' \"$a\"; done\n");
            File.SetUnixFileMode(stubPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            var env = new Dictionary<string, string> { ["PATH"] = stubDir + ":" + Environment.GetEnvironmentVariable("PATH") };
            var (exitCode, stdout, stderr) = await RunShAsync(cmdStr, env);

            Assert.That(exitCode, Is.EqualTo(0), stderr);
            var argv = SplitNulTerminated(stdout);
            Assert.That(argv[0], Is.EqualTo("run"));
            Assert.That(argv[^3..], Is.EqualTo(new[] { "sh", "-c", innerScript }),
                "the nested command must reach docker exactly as written — no outer-shell expansion");
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
