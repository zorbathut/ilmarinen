using Ilmarinen.IntegrationTests.Fixtures;
using NUnit.Framework;
using System.Threading.Tasks;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// What the agent API answers is what the agent script prints: a value byte for byte on success, the server's reason and a failing exit on an error.
/// </summary>
[TestFixture]
[Category("Integration")]
public class AgentScriptResponseTests
{
    [OneTimeSetUp]
    public static async Task OneTimeSetUp()
    {
        await AgentScriptHarness.PullImagesAsync();
    }

    /// <summary>Everything a JSON encoder would have escaped, and the trailing newlines a shell variable would have dropped.</summary>
    private const string HostileValue = "a+b\"c\\d<e>&'f\u001b[0m é ✓\n\n";

    [Test]
    public async Task Secret_PrintsTheValueByteForByte([Values] AgentShell shell)
    {
        var result = await AgentScriptHarness.RunAsync(shell, 200, HostileValue, ["secret", "get", "token"]);

        Assert.That(result.ExitCode, Is.EqualTo(0), $"stderr: {result.Stderr}");
        Assert.That(result.RequestPath, Is.EqualTo("/api/secret/token"));
        Assert.That(result.Stdout, Is.EqualTo(HostileValue));
    }

    /// <summary>curl reads the reason from the body and wget from the status line, so it reaches the user either way.</summary>
    [Test]
    public async Task Secret_ErrorResponse_FailsWithTheServersReason([Values] AgentShell shell)
    {
        const string reason = "Secret 'token' not found";

        var result = await AgentScriptHarness.RunAsync(shell, 404, reason, ["secret", "get", "token"]);

        Assert.That(result.ExitCode, Is.Not.EqualTo(0));
        Assert.That(result.Stdout, Is.Empty);
        Assert.That(result.Stderr, Does.Contain(reason));
    }

    /// <summary>A success cut short is a failure, and what did arrive of a secret must not be printed — not as the value, and not as an error message either, since stderr goes to the job log.</summary>
    [Test]
    public async Task Secret_TruncatedResponse_FailsWithoutPrintingIt([Values] AgentShell shell)
    {
        var result = await AgentScriptHarness.RunAsync(shell, 200, "partial-secret-and-the-rest", ["secret", "get", "token"], truncateAfter: 14);

        Assert.That(result.ExitCode, Is.Not.EqualTo(0));
        Assert.That(result.Stdout + result.Stderr, Does.Not.Contain("partial-secret"));
    }

    /// <summary>An error from the run-command endpoint must not leave the agent exec'ing whatever the body said.</summary>
    [Test]
    public async Task Run_ErrorResponse_FailsWithoutRunningAnything([Values] AgentShell shell)
    {
        var result = await AgentScriptHarness.RunAsync(shell, 503, "echo ran-anyway", ["run", "alpine:latest", "--", "true"]);

        Assert.That(result.ExitCode, Is.Not.EqualTo(0));
        Assert.That(result.Stdout, Does.Not.Contain("ran-anyway"));
        Assert.That(result.Stderr, Does.Contain("echo ran-anyway"), "the body is the server's reason, so it is reported rather than run");
    }

    [Test]
    public async Task Info_PrintsTheValueOnItsOwnLine([Values] AgentShell shell)
    {
        var result = await AgentScriptHarness.RunAsync(shell, 200, "feature/x", ["info", "branch"]);

        Assert.That(result.ExitCode, Is.EqualTo(0), $"stderr: {result.Stderr}");
        Assert.That(result.RequestPath, Is.EqualTo("/api/info/branch"));
        Assert.That(result.Stdout, Is.EqualTo("feature/x\n"));
    }

    /// <summary>Commands that never call the API work without anywhere to put a response.</summary>
    [Test]
    public async Task Version_WorksOnAReadOnlyFilesystem()
    {
        var result = await AgentScriptHarness.RunReadOnlyAsync(["--version"]);

        Assert.That(result.ExitCode, Is.EqualTo(0), $"stderr: {result.Stderr}");
        Assert.That(result.Stdout, Is.Not.Empty);
    }

    [Test]
    public async Task Build_PrintsTheImageReference([Values] AgentShell shell)
    {
        var result = await AgentScriptHarness.RunAsync(shell, 200, "ilmarinen-build:abc123", ["build", "-t", "ilmarinen-build:abc123"]);

        Assert.That(result.ExitCode, Is.EqualTo(0), $"stderr: {result.Stderr}");
        Assert.That(result.RequestPath, Is.EqualTo("/api/build"));
        Assert.That(result.Stdout, Is.EqualTo("ilmarinen-build:abc123\n"));
    }
}
