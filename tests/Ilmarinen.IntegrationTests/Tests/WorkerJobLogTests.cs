using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using NUnit.Framework;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// Verifies that worker-side activity during a job — progress logging and job-killing exceptions — shows up in the job's log output, not just in the worker's own log.
/// </summary>
[TestFixture]
[Category("Integration")]
public class WorkerJobLogTests
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
        _repo.AddFile("pipeline.csx", """
            Step("hello")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Exec("echo", "Hello from integration test!");
                });
            """);
        _repo.Commit("Add pipeline");
    }

    [TearDown]
    public async Task TearDown()
    {
        _repo.Dispose();
        await _fixture.DisposeAsync();
    }

    [Test]
    public async Task JobFailingBeforePipeline_ExceptionAppearsInJobLog()
    {
        var submission = new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "no-such-ref",
            ScriptPath = "pipeline.csx"
        };

        var jobId = await _fixture.SubmitJobAsync(submission);
        var completedJob = await _fixture.WaitForJobCompletionAsync(jobId);

        Assert.That(completedJob.Status, Is.EqualTo(JobStatus.Failed));

        var logs = await WaitForLogContentAsync(jobId, "Could not find ref");
        Assert.That(logs, Does.Contain("failed with exception"));
        Assert.That(logs, Does.Contain("Could not find ref"));
    }

    [Test]
    public async Task SuccessfulJob_WorkerProgressAppearsInJobLog()
    {
        var submission = new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        };

        var jobId = await _fixture.SubmitJobAsync(submission);
        var completedJob = await _fixture.WaitForJobCompletionAsync(jobId);

        Assert.That(completedJob.Status, Is.EqualTo(JobStatus.Success));

        var logs = await WaitForLogContentAsync(jobId, "Cloning");
        Assert.That(logs, Does.Contain("Cloning"));
        Assert.That(logs, Does.Contain("Hello from integration test!"));
    }

    /// <summary>
    /// The server persists the terminal job status before it flushes buffered log chunks to the database, so poll briefly for the expected content instead of asserting on the first read. Returns the last logs read either way; the caller's asserts produce the real failure message.
    /// </summary>
    private async Task<string> WaitForLogContentAsync(Guid jobId, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        string logs;

        while (true)
        {
            logs = await _fixture.GetJobLogsAsync(jobId);
            if (logs.Contains(expected) || DateTime.UtcNow >= deadline)
            {
                return logs;
            }

            await Task.Delay(250);
        }
    }
}
