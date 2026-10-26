using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Linq;
using System.Net.Http.Json;
using System.Net;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// A pipeline's notification policy decides which of its jobs' completions reach subscribers. Jobs are finished through the scheduler directly, so no worker is involved.
/// </summary>
[TestFixture]
[Category("Integration")]
public class NotificationPolicyTests
{
    private IntegrationTestFixture _fixture = null!;
    private SubscriberInfo _subscriber = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new IntegrationTestFixture();
        await _fixture.SetupAsync();
        _subscriber = await _fixture.RegisterSubscriberAsync(new SubscriberRegistration { Name = "policy-test" });
    }

    [TearDown]
    public async Task TearDown()
    {
        await _fixture.DisposeAsync();
    }

    [TestCase(NotificationPolicy.All, JobStatus.Success, 1)]
    [TestCase(NotificationPolicy.All, JobStatus.Failed, 1)]
    [TestCase(NotificationPolicy.FailuresOnly, JobStatus.Success, 0)]
    [TestCase(NotificationPolicy.FailuresOnly, JobStatus.Failed, 1)]
    [TestCase(NotificationPolicy.None, JobStatus.Success, 0)]
    [TestCase(NotificationPolicy.None, JobStatus.Failed, 0)]
    public async Task Completion_NotifiesAsThePipelinePolicyAllows(NotificationPolicy policy, JobStatus status, int expected)
    {
        var jobId = await SubmitAsync(await CreatePipelineAsync(policy));

        await CompleteAsync(jobId, status);

        Assert.That(await NotificationCountAsync(jobId), Is.EqualTo(expected));
    }

    /// <summary>Someone cancelled the job on purpose; that isn't the kind of failure a failures-only pipeline is asking to hear about.</summary>
    [TestCase(NotificationPolicy.All, 1)]
    [TestCase(NotificationPolicy.FailuresOnly, 0)]
    [TestCase(NotificationPolicy.None, 0)]
    public async Task Cancel_NotifiesAsThePipelinePolicyAllows(NotificationPolicy policy, int expected)
    {
        var jobId = await SubmitAsync(await CreatePipelineAsync(policy));

        await CancelAsync(jobId);

        Assert.That(await NotificationCountAsync(jobId), Is.EqualTo(expected));
    }

    /// <summary>A worker that had already finished reports its real outcome after the cancel, and that outcome is judged on its own.</summary>
    [TestCase(JobStatus.Failed, 1)]
    [TestCase(JobStatus.Success, 0)]
    public async Task OutcomeOverridingACancel_UnderFailuresOnly_IsJudgedOnItsOwn(JobStatus outcome, int expected)
    {
        var jobId = await SubmitAsync(await CreatePipelineAsync(NotificationPolicy.FailuresOnly));

        await CancelAsync(jobId);
        await CompleteAsync(jobId, outcome);

        Assert.That((await _fixture.GetJobAsync(jobId)).Status, Is.EqualTo(outcome));
        Assert.That(await NotificationCountAsync(jobId), Is.EqualTo(expected));
    }

    /// <summary>The policy is read when the job finishes, so muting a pipeline also quiets the jobs it already has running.</summary>
    [Test]
    public async Task PolicyChangedWhileAJobRuns_AppliesToThatJob()
    {
        var pipeline = await CreatePipelineAsync(NotificationPolicy.All);
        var jobId = await SubmitAsync(pipeline);

        await _fixture.UpdatePipelineAsync(pipeline.Id, new PipelineUpdate { NotificationPolicy = NotificationPolicy.None });
        await CompleteAsync(jobId, JobStatus.Failed);

        Assert.That(await NotificationCountAsync(jobId), Is.Zero);
    }

    [Test]
    public async Task JobWithoutAPipeline_IsAnnounced()
    {
        var jobId = await JobLogSeed.CreateJobAsync(_fixture);

        await CompleteAsync(jobId, JobStatus.Success);

        Assert.That(await NotificationCountAsync(jobId), Is.EqualTo(1));
    }

    [Test]
    public async Task Policy_RoundTripsThroughTheApi()
    {
        var created = await CreatePipelineAsync(NotificationPolicy.FailuresOnly);
        Assert.That(created.NotificationPolicy, Is.EqualTo(NotificationPolicy.FailuresOnly));

        var updated = await _fixture.UpdatePipelineAsync(created.Id, new PipelineUpdate { NotificationPolicy = NotificationPolicy.None });
        Assert.That(updated.NotificationPolicy, Is.EqualTo(NotificationPolicy.None));
        Assert.That((await _fixture.GetPipelineAsync(created.Id)).NotificationPolicy, Is.EqualTo(NotificationPolicy.None));

        var untouched = await _fixture.UpdatePipelineAsync(created.Id, new PipelineUpdate { Name = "renamed" });
        Assert.That(untouched.NotificationPolicy, Is.EqualTo(NotificationPolicy.None), "an update that doesn't mention the policy leaves it alone");
    }

    [Test]
    public async Task Policy_DefaultsToAll()
    {
        var repository = await _fixture.CreateRepositoryAsync(new RepositorySubmission { Name = $"repo-{Guid.NewGuid():N}", RepoUrl = "https://example.invalid/repo.git" });
        var pipeline = await _fixture.CreatePipelineAsync(new PipelineSubmission
        {
            Name = $"pipeline-{Guid.NewGuid():N}",
            RepositoryId = repository.Id,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        Assert.That(pipeline.NotificationPolicy, Is.EqualTo(NotificationPolicy.All));
    }

    [Test]
    public async Task Policy_OutOfRange_IsRejected()
    {
        var pipeline = await CreatePipelineAsync(NotificationPolicy.All);

        var response = await _fixture.HttpClient.PutAsJsonAsync($"/api/pipelines/{pipeline.Id}", new { notificationPolicy = 7 });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    private async Task<PipelineInfo> CreatePipelineAsync(NotificationPolicy policy)
    {
        var repository = await _fixture.CreateRepositoryAsync(new RepositorySubmission { Name = $"repo-{Guid.NewGuid():N}", RepoUrl = "https://example.invalid/repo.git" });
        return await _fixture.CreatePipelineAsync(new PipelineSubmission
        {
            Name = $"pipeline-{Guid.NewGuid():N}",
            RepositoryId = repository.Id,
            Ref = "master",
            ScriptPath = "pipeline.csx",
            NotificationPolicy = policy
        });
    }

    private async Task<Guid> SubmitAsync(PipelineInfo pipeline)
    {
        return await _fixture.TriggerPipelineAsync(pipeline.Id);
    }

    private async Task CompleteAsync(Guid jobId, JobStatus status)
    {
        using var scope = _fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<JobScheduler>().CompleteJobAsync(jobId, status);
    }

    private async Task CancelAsync(Guid jobId)
    {
        Assert.That(await _fixture.CancelJobAsync(jobId), Is.True);
    }

    private async Task<int> NotificationCountAsync(Guid jobId)
    {
        var notifications = await _fixture.PullNotificationsAsync(_subscriber.Id, limit: 100);
        return notifications.Count(n => n.JobId == jobId);
    }
}
