using Ilmarinen.Docker;
using NUnit.Framework;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Fixtures;

/// <summary>
/// Runs the real in-container agent script against a loopback server that answers its one request as told, so the script's own encoding and response handling can be checked without a pipeline around it.
///
/// The script meets arbitrary images, so each test runs it under every <see cref="AgentShell"/>.
/// </summary>
public static class AgentScriptHarness
{
    public sealed record Result(int ExitCode, string Stdout, string Stderr, string RequestPath, string RequestBody);

    /// <param name="truncateAfter">When set, the response declares its full length but the connection closes after this many body bytes, as a server or network failing mid-response would.</param>
    public static async Task<Result> RunAsync(AgentShell shell, int responseStatus, string responseBody, string[] args, int? truncateAfter = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var requestTask = HandleOneRequestAsync(listener, responseStatus, responseBody, truncateAfter);

            var scriptPath = Path.Combine(Path.GetTempPath(), $"ilmarinen-agent-test-{Guid.NewGuid():N}.sh");
            await File.WriteAllTextAsync(scriptPath, PipelineRunner.ShellScript);
            try
            {
                var api = $"http://127.0.0.1:{port}";
                ProcessStartInfo psi;
                if (shell == AgentShell.HostSh)
                {
                    psi = new ProcessStartInfo("/bin/sh");
                    psi.ArgumentList.Add(scriptPath);
                    psi.Environment["ILMARINEN_API"] = api;
                    psi.Environment["ILMARINEN_TOKEN"] = "test-token";
                }
                else
                {
                    // --network host so the container reaches the loopback server.
                    psi = new ProcessStartInfo("docker");
                    foreach (var dockerArg in new[] { "run", "--rm", "--network", "host", "-e", $"ILMARINEN_API={api}", "-e", "ILMARINEN_TOKEN=test-token", "-v", $"{scriptPath}:/ilmarinen-agent:ro" }.Concat(ContainerCommand(shell)))
                    {
                        psi.ArgumentList.Add(dockerArg);
                    }
                }

                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                foreach (var arg in args)
                {
                    psi.ArgumentList.Add(arg);
                }

                using var process = Process.Start(psi)!;
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                // Race the request against process exit so a script that dies before sending fails with its stderr instead of a bare timeout.
                var exitTask = process.WaitForExitAsync();
                if (await Task.WhenAny(requestTask, exitTask) == exitTask && !requestTask.IsCompleted)
                {
                    Assert.Fail($"script exited with code {process.ExitCode} without sending a request\nstderr: {await stderrTask}\nstdout: {await stdoutTask}");
                }

                var (path, body) = await requestTask.WaitAsync(TimeSpan.FromSeconds(60));
                await exitTask.WaitAsync(TimeSpan.FromSeconds(60));

                return new Result(process.ExitCode, await stdoutTask, await stderrTask, path, body);
            }
            finally
            {
                File.Delete(scriptPath);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Pulls the images the container shells run in, so that a first, cold pull isn't counted against a test's own waits.</summary>
    public static async Task PullImagesAsync()
    {
        foreach (var image in new[] { "alpine:latest", "buildpack-deps:bookworm-curl" })
        {
            var psi = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("pull");
            psi.ArgumentList.Add(image);

            using var process = Process.Start(psi)!;
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.That(process.ExitCode, Is.EqualTo(0), $"docker pull {image} failed: {await stderrTask}{await stdoutTask}");
        }
    }

    /// <summary>The image and command line that run the script under <paramref name="shell"/>.</summary>
    private static string[] ContainerCommand(AgentShell shell)
    {
        return shell switch
        {
            AgentShell.BusyboxAlpine => ["alpine:latest", "sh", "/ilmarinen-agent"],
            AgentShell.DebianDash => ["buildpack-deps:bookworm-curl", "sh", "/ilmarinen-agent"],
            // The script prefers curl, so taking it away is what puts it on GNU wget.
            AgentShell.DebianDashWget => ["buildpack-deps:bookworm-curl", "sh", "-c", "rm -f /usr/bin/curl && exec sh /ilmarinen-agent \"$@\"", "--"],
            _ => throw new ArgumentOutOfRangeException(nameof(shell), shell, "not a container shell")
        };
    }

    /// <summary>Runs the script in busybox Alpine on a read-only root filesystem, with no API behind it, for commands that shouldn't need either.</summary>
    public static async Task<Result> RunReadOnlyAsync(string[] args)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"ilmarinen-agent-test-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(scriptPath, PipelineRunner.ShellScript);
        try
        {
            var psi = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in new[] { "run", "--rm", "--read-only", "-v", $"{scriptPath}:/ilmarinen-agent:ro", "alpine:latest", "sh", "/ilmarinen-agent" }.Concat(args))
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi)!;
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));

            return new Result(process.ExitCode, await stdoutTask, await stderrTask, "", "");
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    /// <summary>Accepts one HTTP request, answers it with <paramref name="status"/> and <paramref name="body"/> as plain text — an error's body doubling as its reason phrase, as AgentApiServer does — and returns its path and body.</summary>
    private static async Task<(string Path, string Body)> HandleOneRequestAsync(TcpListener listener, int status, string body, int? truncateAfter)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();

        var data = new byte[1 << 20];
        var length = 0;
        int bodyStart;
        while ((bodyStart = IndexOfHeaderTerminator(data, length)) < 0)
        {
            var n = await stream.ReadAsync(data.AsMemory(length));
            Assert.That(n, Is.GreaterThan(0), "connection closed before the request headers arrived");
            length += n;
        }

        var headers = Encoding.ASCII.GetString(data, 0, bodyStart);
        if (headers.Contains("Expect: 100-continue", StringComparison.OrdinalIgnoreCase))
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"));
        }

        var lines = headers.Split("\r\n");
        var path = lines[0].Split(' ')[1];
        var contentLengthHeader = lines.FirstOrDefault(h => h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        var contentLength = contentLengthHeader != null ? int.Parse(contentLengthHeader["Content-Length:".Length..].Trim()) : 0;
        while (length - bodyStart < contentLength)
        {
            var n = await stream.ReadAsync(data.AsMemory(length));
            Assert.That(n, Is.GreaterThan(0), "connection closed before the request body arrived");
            length += n;
        }

        var responseBytes = Encoding.UTF8.GetBytes(body);
        var reason = status >= 400 ? body : "OK";
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {responseBytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(responseBytes.AsMemory(0, truncateAfter ?? responseBytes.Length));

        return (path, Encoding.UTF8.GetString(data, bodyStart, contentLength));
    }

    private static int IndexOfHeaderTerminator(byte[] data, int length)
    {
        for (var i = 0; i + 3 < length; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
            {
                return i + 4;
            }
        }
        return -1;
    }
}

/// <summary>Where the agent script runs: the shell, and the HTTP client it finds there.</summary>
public enum AgentShell
{
    /// <summary>The host's /bin/sh, with the host's curl.</summary>
    HostSh,

    /// <summary>busybox sh and busybox wget, as in alpine-based images such as docker:cli.</summary>
    BusyboxAlpine,

    /// <summary>dash with curl, as in Debian- and Ubuntu-based images.</summary>
    DebianDash,

    /// <summary>dash with GNU wget and no curl.</summary>
    DebianDashWget
}
