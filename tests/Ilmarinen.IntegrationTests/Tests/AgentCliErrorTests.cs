using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using NUnit.Framework;
using System.Threading.Tasks;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
[Category("Integration")]
public class AgentCliErrorTests
{
    private IntegrationTestFixture _fixture = null!;
    private TestGitRepository _repo = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new IntegrationTestFixture();
        await _fixture.SetupAsync();
        await _fixture.StartWorkerAsync();
        _repo = new TestGitRepository();
    }

    [TearDown]
    public async Task TearDown()
    {
        _repo.Dispose();
        await _fixture.DisposeAsync();
    }

    [Test]
    public async Task AgentCli_ApiErrors_ReportsStructuredErrorDetails()
    {
        // The agent CLI (ilmarinen-agent) communicates with the API server via HTTP.
        // When the API returns an error (404, 422, etc.), the CLI must surface the
        // structured error details — not silently swallow them via set -e or curl -f.
        //
        // Each sub-test calls an agent CLI command that triggers an API error, then
        // asserts that: (a) exit code is non-zero, (b) stderr contains error details.
        // If error details are lost (silent failure), the step throws and the job fails.
        _repo.AddFile("pipeline.csx", """
            Step("test-error-reporting")
                .Image("docker:cli")
                .Run(async ctx => {
                    // Test 1: info with invalid key → API returns 404
                    var r = await ctx.TryShell("ilmarinen-agent info badkey");
                    if (r.ExitCode == 0)
                        throw new Exception("info: expected non-zero exit for invalid key");
                    if (string.IsNullOrWhiteSpace(r.Stderr))
                        throw new Exception($"info: error details lost (exit {r.ExitCode}, stderr empty)");

                    // Test 2: secret not found → API returns 404
                    r = await ctx.TryShell("ilmarinen-agent secret get nonexistent_secret");
                    if (r.ExitCode == 0)
                        throw new Exception("secret: expected non-zero exit for missing secret");
                    if (string.IsNullOrWhiteSpace(r.Stderr))
                        throw new Exception($"secret: error details lost (exit {r.ExitCode}, stderr empty)");

                    // Test 3: build with missing Dockerfile → API returns 422
                    r = await ctx.TryShell("ilmarinen-agent build -f /nonexistent/Dockerfile");
                    if (r.ExitCode == 0)
                        throw new Exception("build: expected non-zero exit for missing Dockerfile");
                    if (string.IsNullOrWhiteSpace(r.Stderr))
                        throw new Exception($"build: error details lost (exit {r.ExitCode}, stderr empty)");
                });
            """);
        _repo.Commit("Add agent CLI error reporting test");

        var jobId = await _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        var completedJob = await _fixture.WaitForJobCompletionAsync(jobId);
        Assert.That(completedJob.Status, Is.EqualTo(JobStatus.Success),
            "Agent CLI should report structured error details instead of failing silently");
    }
}
