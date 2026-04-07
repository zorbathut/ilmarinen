using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using NUnit.Framework;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
[Category("Integration")]
public class JobRetryTests
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
            Step("hello")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Exec("echo", "Hello from retry test!");
                });
            """);
        _repo.AddFile("failing.csx", """
            Step("fail")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Exec("false");
                });
            """);
        _repo.Commit("Add pipelines");
    }

    [TearDown]
    public async Task TearDown()
    {
        _repo.Dispose();
        await _fixture.DisposeAsync();
    }

    [Test]
    public async Task RetryFailedJob_NoneMode_CreatesNewJob()
    {
        await _fixture.StartWorkerAsync();

        var jobId = await _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "failing.csx",
            GitTokenMode = GitTokenMode.None
        });

        var failedJob = await _fixture.WaitForJobCompletionAsync(jobId);
        Assert.That(failedJob.Status, Is.EqualTo(JobStatus.Failed));
        Assert.That(failedJob.CanRetry, Is.True);

        var retryJobId = await _fixture.RetryJobAsync(jobId);
        Assert.That(retryJobId, Is.Not.EqualTo(jobId));

        var retryJob = await _fixture.GetJobAsync(retryJobId);
        Assert.That(retryJob.RepoUrl, Is.EqualTo(failedJob.RepoUrl));
        Assert.That(retryJob.Ref, Is.EqualTo(failedJob.Ref));
        Assert.That(retryJob.ScriptPath, Is.EqualTo(failedJob.ScriptPath));
        Assert.That(retryJob.GitTokenMode, Is.EqualTo(GitTokenMode.None));
    }

    [Test]
    public async Task RetryFailedJob_InheritMode_CreatesNewJob()
    {
        await _fixture.StartWorkerAsync();

        var repository = await _fixture.CreateRepositoryAsync(new RepositorySubmission
        {
            Name = "retry-inherit-repo",
            RepoUrl = _repo.Url
        });

        var pipeline = await _fixture.CreatePipelineAsync(new PipelineSubmission
        {
            Name = "retry-inherit-pipeline",
            RepositoryId = repository.Id,
            Ref = "master",
            ScriptPath = "failing.csx"
        });

        // Trigger via pipeline — uses Inherit mode
        var jobId = await _fixture.TriggerPipelineAsync(pipeline.Id);
        var failedJob = await _fixture.WaitForJobCompletionAsync(jobId);

        Assert.That(failedJob.Status, Is.EqualTo(JobStatus.Failed));
        Assert.That(failedJob.GitTokenMode, Is.EqualTo(GitTokenMode.Inherit));
        Assert.That(failedJob.CanRetry, Is.True);

        var retryJobId = await _fixture.RetryJobAsync(jobId);
        Assert.That(retryJobId, Is.Not.EqualTo(jobId));

        var retryJob = await _fixture.GetJobAsync(retryJobId);
        Assert.That(retryJob.PipelineId, Is.EqualTo(pipeline.Id));
        Assert.That(retryJob.GitTokenMode, Is.EqualTo(GitTokenMode.Inherit));
    }

    [Test]
    public async Task RetryExplicitTokenJob_Returns400()
    {
        await _fixture.StartWorkerAsync();

        var jobId = await _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "failing.csx",
            GitTokenMode = GitTokenMode.Explicit,
            GitToken = "fake-token-for-test"
        });

        var failedJob = await _fixture.WaitForJobCompletionAsync(jobId);
        Assert.That(failedJob.Status, Is.EqualTo(JobStatus.Failed));
        Assert.That(failedJob.CanRetry, Is.False);
        Assert.That(failedJob.RetryBlockedReason, Does.Contain("token"));

        var response = await _fixture.HttpClient.PostAsync($"/api/jobs/{jobId}/retry", null);
        Assert.That((int)response.StatusCode, Is.EqualTo(400));
    }

    [Test]
    public async Task RetryRunningJob_Returns404()
    {
        await _fixture.StartWorkerAsync();

        _repo.AddFile("slow.csx", """
            Step("slow")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Exec("sleep", "30");
                });
            """);
        _repo.Commit("Add slow pipeline");

        var jobId = await _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "slow.csx"
        });

        // Wait for it to start running
        await Task.Delay(3000);

        var response = await _fixture.HttpClient.PostAsync($"/api/jobs/{jobId}/retry", null);
        Assert.That((int)response.StatusCode, Is.EqualTo(404));
    }

    [Test]
    public async Task RetrySucceededJob_Returns404()
    {
        await _fixture.StartWorkerAsync();

        var jobId = await _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        var completedJob = await _fixture.WaitForJobCompletionAsync(jobId);
        Assert.That(completedJob.Status, Is.EqualTo(JobStatus.Success));
        Assert.That(completedJob.CanRetry, Is.False);

        var response = await _fixture.HttpClient.PostAsync($"/api/jobs/{jobId}/retry", null);
        Assert.That((int)response.StatusCode, Is.EqualTo(404));
    }

    [Test]
    public async Task JobInfo_FailedNoneMode_ShowsCanRetry()
    {
        await _fixture.StartWorkerAsync();

        var jobId = await _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "failing.csx"
        });

        var failedJob = await _fixture.WaitForJobCompletionAsync(jobId);

        Assert.That(failedJob.CanRetry, Is.True);
        Assert.That(failedJob.RetryBlockedReason, Is.Null);
    }
}
