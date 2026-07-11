using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using NUnit.Framework;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
[Category("Integration")]
public class WorkerConnectionTests
{
    private IntegrationTestFixture _fixture = null!;
    private TestGitRepository _repo = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new IntegrationTestFixture();
        await _fixture.SetupAsync();

        _repo = new TestGitRepository();
        _repo.AddFile("pipeline.csx", """
            Step("quick")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Exec("echo", "done");
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
    public async Task Worker_WhenStarted_RegistersWithServer()
    {
        // Act
        var workerId = await _fixture.StartWorkerAsync();

        // Assert - Worker registration succeeded (would have thrown if not)
        Assert.That(workerId, Is.Not.EqualTo(Guid.Empty));
    }

    [Test]
    public async Task Job_WhenWorkerNotAvailable_StaysQueued()
    {
        // Arrange - No worker started
        var submission = new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        };

        // Act
        var jobId = await _fixture.SubmitJobAsync(submission);
        await Task.Delay(1000); // Give it time to potentially (incorrectly) run

        // Assert
        var job = await _fixture.GetJobAsync(jobId);
        Assert.That(job.Status, Is.EqualTo(JobStatus.Queued));
    }

    [Test]
    public async Task Job_WhenWorkerStartsLater_GetsExecuted()
    {
        // Arrange - Submit job first (no worker yet)
        var submission = new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        };
        var jobId = await _fixture.SubmitJobAsync(submission);

        // Verify queued
        var queuedJob = await _fixture.GetJobAsync(jobId);
        Assert.That(queuedJob.Status, Is.EqualTo(JobStatus.Queued));

        // Act - Start worker
        await _fixture.StartWorkerAsync();

        // Assert - Job should complete
        var completedJob = await _fixture.WaitForJobCompletionAsync(jobId);
        Assert.That(completedJob.Status, Is.EqualTo(JobStatus.Success));
    }
}
