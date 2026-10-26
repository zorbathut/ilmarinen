using Ilmarinen.IntegrationTests.Fixtures;
using NUnit.Framework;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// The in-container agent script builds JSON payloads for the agent API by hand, so its escaping
/// must produce valid JSON for any argument a pipeline passes through it. This runs the real
/// script against a loopback capture server and asserts the payload parses and the arguments
/// round-trip byte-for-byte.
/// </summary>
[TestFixture]
[Category("Integration")]
public class AgentScriptJsonTests
{
    [OneTimeSetUp]
    public static async Task OneTimeSetUp()
    {
        await AgentScriptHarness.PullImagesAsync();
    }

    /// <summary>
    /// The busybox variant matters: the script runs inside arbitrary user images, and POSIX awks (busybox, mawk) define gsub replacement escapes differently from gawk — an escaping program that is only exercised under the host's gawk can pass here and still corrupt JSON in real containers.
    /// </summary>
    [Test]
    public async Task AgentRun_EncodesHostileArgumentsAsValidJson([Values] AgentShell shell)
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

        // The answer is a run command that does nothing, so the script exits cleanly once it has posted.
        var result = await AgentScriptHarness.RunAsync(shell, 200, "true", ["run", "test-image", "--", .. args]);

        Assert.That(result.ExitCode, Is.EqualTo(0), $"stderr: {result.Stderr}\nstdout: {result.Stdout}");

        using var payload = JsonDocument.Parse(result.RequestBody);
        Assert.That(payload.RootElement.GetProperty("image").GetString(), Is.EqualTo("test-image"));
        var sent = payload.RootElement.GetProperty("command").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.That(sent, Is.EqualTo(args), "every argument must round-trip through the agent's JSON encoding byte-for-byte");
    }
}
