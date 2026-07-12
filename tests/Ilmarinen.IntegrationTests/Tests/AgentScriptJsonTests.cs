using Ilmarinen.Docker;
using NUnit.Framework;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// The in-container agent script builds JSON payloads for the agent API by hand, so its escaping
/// must produce valid JSON for any argument a pipeline passes through it. This runs the real
/// script under /bin/sh against a loopback capture server and asserts the payload parses and the
/// arguments round-trip byte-for-byte.
/// </summary>
[TestFixture]
[Category("Integration")]
public class AgentScriptJsonTests
{
    /// <summary>
    /// The busybox variant matters: the script runs inside arbitrary user images, and POSIX awks (busybox, mawk) define gsub replacement escapes differently from gawk — an escaping program that is only exercised under the host's gawk can pass here and still corrupt JSON in real containers.
    /// </summary>
    [TestCase(false, TestName = "AgentRun_EncodesHostileArgumentsAsValidJson_HostShell")]
    [TestCase(true, TestName = "AgentRun_EncodesHostileArgumentsAsValidJson_BusyboxAlpine")]
    public async Task AgentRun_EncodesHostileArgumentsAsValidJson(bool useAlpineContainer)
    {
        var args = new[]
        {
            "plain",
            "with space",
            "quote\"quote",
            "back\\slash",
            "line1\nline2",
            "tab\there",
            "ec=$?; exit $ec",
        };

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var bodyTask = HandleOneRequestAsync(listener);

            var scriptPath = Path.Combine(Path.GetTempPath(), $"ilmarinen-agent-test-{Guid.NewGuid():N}.sh");
            await File.WriteAllTextAsync(scriptPath, PipelineRunner.ShellScript);
            try
            {
                var api = $"http://127.0.0.1:{port}";
                ProcessStartInfo psi;
                if (useAlpineContainer)
                {
                    // --network host so the container reaches the loopback capture server.
                    psi = new ProcessStartInfo("docker")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    foreach (var dockerArg in new[] { "run", "--rm", "--network", "host", "-e", $"ILMARINEN_API={api}", "-e", "ILMARINEN_TOKEN=test-token", "-v", $"{scriptPath}:/ilmarinen-agent:ro", "alpine:latest", "sh", "/ilmarinen-agent" })
                    {
                        psi.ArgumentList.Add(dockerArg);
                    }
                }
                else
                {
                    psi = new ProcessStartInfo("/bin/sh")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    psi.ArgumentList.Add(scriptPath);
                    psi.Environment["ILMARINEN_API"] = api;
                    psi.Environment["ILMARINEN_TOKEN"] = "test-token";
                }
                psi.ArgumentList.Add("run");
                psi.ArgumentList.Add("test-image");
                psi.ArgumentList.Add("--");
                foreach (var arg in args)
                {
                    psi.ArgumentList.Add(arg);
                }

                using var process = Process.Start(psi)!;
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                // Race the capture against process exit so a script that dies before posting fails with its stderr instead of a bare timeout.
                var exitTask = process.WaitForExitAsync();
                if (await Task.WhenAny(bodyTask, exitTask) == exitTask && !bodyTask.IsCompleted)
                {
                    Assert.Fail($"script exited with code {process.ExitCode} without posting a request\nstderr: {await stderrTask}\nstdout: {await stdoutTask}");
                }
                var body = await bodyTask.WaitAsync(TimeSpan.FromSeconds(60));
                await exitTask.WaitAsync(TimeSpan.FromSeconds(60));
                Assert.That(process.ExitCode, Is.EqualTo(0), $"stderr: {await stderrTask}\nstdout: {await stdoutTask}");

                using var payload = JsonDocument.Parse(body);
                Assert.That(payload.RootElement.GetProperty("image").GetString(), Is.EqualTo("test-image"));
                var sent = payload.RootElement.GetProperty("command").EnumerateArray().Select(e => e.GetString()).ToArray();
                Assert.That(sent, Is.EqualTo(args), "every argument must round-trip through the agent's JSON encoding byte-for-byte");
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

    /// <summary>
    /// Accepts one HTTP request, returns its body, and answers with the NDJSON exit message the
    /// script's stream parser expects.
    /// </summary>
    private static async Task<string> HandleOneRequestAsync(TcpListener listener)
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

        var contentLength = int.Parse(headers
            .Split("\r\n")
            .First(h => h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))["Content-Length:".Length..]
            .Trim());
        while (length - bodyStart < contentLength)
        {
            var n = await stream.ReadAsync(data.AsMemory(length));
            Assert.That(n, Is.GreaterThan(0), "connection closed before the request body arrived");
            length += n;
        }

        const string ndjson = "{\"t\":\"x\",\"c\":0}\n";
        var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/x-ndjson\r\nContent-Length: {Encoding.ASCII.GetByteCount(ndjson)}\r\nConnection: close\r\n\r\n{ndjson}";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response));

        return Encoding.UTF8.GetString(data, bodyStart, contentLength);
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
