using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using NUnit.Framework;
using System.Threading.Tasks;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
[Category("Integration")]
public class BasicJobFlowTests
{
    private IntegrationTestFixture _fixture = null!;
    private TestGitRepository _repo = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new IntegrationTestFixture();
        await _fixture.SetupAsync();
        await _fixture.StartWorkerAsync();

        // Create test repository with a simple pipeline
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
    public async Task SubmitJob_ExecutesPipeline_ReturnsSuccess()
    {
        // Arrange
        var submission = new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        };

        // Act
        var jobId = await _fixture.SubmitJobAsync(submission);
        var completedJob = await _fixture.WaitForJobCompletionAsync(jobId);

        // Assert
        Assert.That(completedJob.Status, Is.EqualTo(JobStatus.Success));
        Assert.That(completedJob.StartedAt, Is.Not.Null);
        Assert.That(completedJob.CompletedAt, Is.Not.Null);
        Assert.That(completedJob.WorkerId, Is.Not.Null);
    }

    [Test]
    public async Task SubmitJob_WithFailingPipeline_ReturnsFailed()
    {
        // Arrange - create a failing pipeline
        using var failingRepo = new TestGitRepository();
        failingRepo.AddFile("failing.csx", """
            Step("fail")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Shell("exit 1");
                });
            """);
        failingRepo.Commit("Add failing pipeline");

        var submission = new JobSubmission
        {
            RepoUrl = failingRepo.Url,
            Ref = "master",
            ScriptPath = "failing.csx"
        };

        // Act
        var jobId = await _fixture.SubmitJobAsync(submission);
        var completedJob = await _fixture.WaitForJobCompletionAsync(jobId);

        // Assert
        Assert.That(completedJob.Status, Is.EqualTo(JobStatus.Failed));
    }

    [Test]
    public async Task GetJob_AfterSubmission_ReturnsJobInfo()
    {
        // Arrange
        var submission = new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        };

        // Act
        var jobId = await _fixture.SubmitJobAsync(submission);
        var job = await _fixture.GetJobAsync(jobId);

        // Assert
        Assert.That(job.Id, Is.EqualTo(jobId));
        Assert.That(job.RepoUrl, Is.EqualTo(submission.RepoUrl));
        Assert.That(job.Ref, Is.EqualTo(submission.Ref));
        Assert.That(job.ScriptPath, Is.EqualTo(submission.ScriptPath));
    }
}
