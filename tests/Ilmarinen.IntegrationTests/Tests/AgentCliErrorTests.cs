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
    public async Task AgentCli_ApiErrors_ReportTheServersReason()
    {
        // The agent CLI (ilmarinen-agent) communicates with the API server via HTTP. When the API returns an error (404, 503, etc.), the CLI must surface the server's reason — not silently swallow it via set -e, or replace it with a generic failure. docker:cli has only busybox wget, which is the harder of the agent's two HTTP paths to get a reason out of.
        //
        // Each sub-test calls an agent CLI command that triggers an API error, then asserts that: (a) exit code is non-zero, (b) stderr carries the server's reason. If it is lost, the step throws and the job fails.
        _repo.AddFile("pipeline.csx", """
            Step("test-error-reporting")
                .Image("docker:cli")
                .Run(async ctx => {
                    // Test 1: info with invalid key → API returns 404
                    var r = await ctx.TryShell("ilmarinen-agent info badkey");
                    if (r.ExitCode == 0)
                        throw new Exception("info: expected non-zero exit for invalid key");
                    if (!r.Stderr.Contains("badkey"))
                        throw new Exception($"info: server's reason lost (exit {r.ExitCode}, stderr: {r.Stderr})");

                    // Test 2: secret not found → API returns 404
                    r = await ctx.TryShell("ilmarinen-agent secret get nonexistent_secret");
                    if (r.ExitCode == 0)
                        throw new Exception("secret: expected non-zero exit for missing secret");
                    if (!r.Stderr.Contains("nonexistent_secret"))
                        throw new Exception($"secret: server's reason lost (exit {r.ExitCode}, stderr: {r.Stderr})");

                    // Test 3: build with missing Dockerfile → docker build itself fails
                    r = await ctx.TryShell("ilmarinen-agent build -f /nonexistent/Dockerfile");
                    if (r.ExitCode == 0)
                        throw new Exception("build: expected non-zero exit for missing Dockerfile");
                    if (!r.Stderr.Contains("/nonexistent"))
                        throw new Exception($"build: docker's reason lost (exit {r.ExitCode}, stderr: {r.Stderr})");
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
            "Agent CLI should report the server's reason instead of failing silently");
    }
}
