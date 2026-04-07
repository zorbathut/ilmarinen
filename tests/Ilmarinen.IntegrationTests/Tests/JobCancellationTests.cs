using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using NUnit.Framework;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
[Category("Integration")]
public class JobCancellationTests
{
    private IntegrationTestFixture _fixture = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new IntegrationTestFixture();
        await _fixture.SetupAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _fixture.DisposeAsync();
    }

    [Test]
    public async Task CancelJob_WhenQueued_ReturnsCancelled()
    {
        // Note: No worker started - jobs stay queued

        // Arrange - submit without worker running
        using var repo = new TestGitRepository();
        repo.AddFile("pipeline.csx", """
            Step("test")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Exec("echo", "test");
                });
            """);
        repo.Commit("Add pipeline");

        var submission = new JobSubmission
        {
            RepoUrl = repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        };
        var jobId = await _fixture.SubmitJobAsync(submission);

        // Verify it's queued
        var queuedJob = await _fixture.GetJobAsync(jobId);
        Assert.That(queuedJob.Status, Is.EqualTo(JobStatus.Queued));

        // Act
        var cancelled = await _fixture.CancelJobAsync(jobId);

        // Assert
        Assert.That(cancelled, Is.True);
        var cancelledJob = await _fixture.GetJobAsync(jobId);
        Assert.That(cancelledJob.Status, Is.EqualTo(JobStatus.Cancelled));
    }

    [Test]
    public async Task CancelJob_WhenRunning_KillsProcessAndWorkerBecomesReady()
    {
        await _fixture.StartWorkerAsync();

        // Arrange - submit a long-running job
        using var repo = new TestGitRepository();
        repo.AddFile("pipeline.csx", """
            Step("long-running")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Shell("sleep 300");
                });
            """);
        repo.Commit("Add pipeline");

        var submission = new JobSubmission
        {
            RepoUrl = repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        };
        var jobId = await _fixture.SubmitJobAsync(submission);

        // Wait for the job to start running
        await WaitForJobStatusAsync(jobId, JobStatus.Running);

        // Act - cancel the running job
        var cancelled = await _fixture.CancelJobAsync(jobId);
        Assert.That(cancelled, Is.True);

        // Assert - job completes as Cancelled
        var completedJob = await _fixture.WaitForJobCompletionAsync(jobId, timeoutMs: 30000);
        Assert.That(completedJob.Status, Is.EqualTo(JobStatus.Cancelled));

        // Assert - worker is ready again by submitting another job and seeing it complete
        using var repo2 = new TestGitRepository();
        repo2.AddFile("pipeline.csx", """
            Step("quick")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Exec("echo", "worker is ready");
                });
            """);
        repo2.Commit("Add pipeline");

        var submission2 = new JobSubmission
        {
            RepoUrl = repo2.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        };
        var jobId2 = await _fixture.SubmitJobAsync(submission2);
        var completedJob2 = await _fixture.WaitForJobCompletionAsync(jobId2);
        Assert.That(completedJob2.Status, Is.EqualTo(JobStatus.Success));
    }

    private async Task WaitForJobStatusAsync(Guid jobId, JobStatus status, int timeoutMs = 30000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            var job = await _fixture.GetJobAsync(jobId);
            if (job.Status == status)
                return;

            await Task.Delay(250);
        }

        throw new TimeoutException($"Job {jobId} did not reach status {status} within {timeoutMs}ms");
    }
}
