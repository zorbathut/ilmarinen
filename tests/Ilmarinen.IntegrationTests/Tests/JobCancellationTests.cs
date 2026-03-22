using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using NUnit.Framework;
using System.Threading.Tasks;

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
        // Note: Not starting worker - jobs stay queued
    }

    [TearDown]
    public async Task TearDown()
    {
        await _fixture.DisposeAsync();
    }

    [Test]
    public async Task CancelJob_WhenQueued_ReturnsCancelled()
    {
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
}
