using Ilmarinen.Database.Entities;
using Ilmarinen.Database;
using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Ilmarinen.Server;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// How workers, pipelines and jobs refer to the operator's priorities: by name on the way in, with "any" for no minimum worker priority, and what each gets when it names none.
/// </summary>
[TestFixture]
[Category("Integration")]
public class PriorityReferenceTests
{
    private IntegrationTestFixture _fixture = null!;
    private RepositoryInfo _repository = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new IntegrationTestFixture();
        await _fixture.SetupAsync();
        _repository = await _fixture.CreateRepositoryAsync(new RepositorySubmission
        {
            Name = "priority-references",
            RepoUrl = "https://example.invalid/repo.git"
        });
    }

    [TearDown]
    public async Task TearDown()
    {
        await _fixture.DisposeAsync();
    }

    private Task<PipelineInfo> PipelineAsync(string? minWorkerPriority)
    {
        return _fixture.CreatePipelineAsync(new PipelineSubmission
        {
            Name = $"pipeline-{Guid.NewGuid():N}",
            RepositoryId = _repository.Id,
            Ref = "master",
            ScriptPath = "pipeline.csx",
            MinWorkerPriority = minWorkerPriority
        });
    }

    [Test]
    public async Task NamesMatchInAnyCase()
    {
        var jobId = await _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = "https://example.invalid/repo.git",
            Ref = "master",
            ScriptPath = "pipeline.csx",
            MinWorkerPriority = "hIGH",
            Priority = "low"
        });

        var job = await _fixture.GetJobAsync(jobId);
        Assert.That(job.MinWorkerPriority?.Name, Is.EqualTo(StandardPriority.High));
        Assert.That(job.Priority.Name, Is.EqualTo(StandardPriority.Low));
    }

    [Test]
    public async Task ATriggerAskingForAnyWorker_OverridesThePipelinesMinimum()
    {
        var pipeline = await PipelineAsync(StandardPriority.High);

        var jobId = await _fixture.TriggerPipelineAsync(pipeline.Id, new PipelineTrigger { MinWorkerPriority = PriorityNames.AnyWorker });

        Assert.That((await _fixture.GetJobAsync(jobId)).MinWorkerPriority, Is.Null);
    }

    [Test]
    public async Task ARetriedJobWithNoMinimum_KeepsNone_EvenWhenItsPipelineHasGainedOne()
    {
        var pipeline = await PipelineAsync(minWorkerPriority: null);
        var jobId = await _fixture.SubmitJobAsync(new JobSubmission { PipelineId = pipeline.Id, GitTokenMode = GitTokenMode.Inherit });
        await _fixture.UpdatePipelineAsync(pipeline.Id, new PipelineUpdate { MinWorkerPriority = StandardPriority.High });

        // No worker is started, so the job stays queued; cancelling it is enough to make it retryable.
        Assert.That(await _fixture.CancelJobAsync(jobId), Is.True);
        var retried = await _fixture.GetJobAsync(await _fixture.RetryJobAsync(jobId));

        Assert.That(retried.MinWorkerPriority, Is.Null);
    }

    [Test]
    public async Task UpdatingAPipelinesMinimumToAny_ClearsIt_AndLeavingItOut_KeepsIt()
    {
        var pipeline = await PipelineAsync(StandardPriority.High);

        var kept = await _fixture.UpdatePipelineAsync(pipeline.Id, new PipelineUpdate { Name = "renamed-pipeline" });
        var cleared = await _fixture.UpdatePipelineAsync(pipeline.Id, new PipelineUpdate { MinWorkerPriority = PriorityNames.AnyWorker });

        Assert.That(kept.MinWorkerPriority?.Name, Is.EqualTo(StandardPriority.High));
        Assert.That(cleared.MinWorkerPriority, Is.Null);
    }

    [Test]
    public async Task AWriteNamingAPriorityDeletedMeanwhile_IsTheCallersMistake_NotAServerError()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        db.Jobs.Add(new Job
        {
            Id = Guid.CreateVersion7(),
            Status = JobStatus.Queued,
            RepoUrl = "https://example.invalid/repo.git",
            Ref = "master",
            ScriptPath = "pipeline.csx",
            CreatedAt = DateTime.UtcNow,
            PriorityId = Guid.NewGuid()
        });

        Assert.ThrowsAsync<InvalidSubmissionException>(() => PriorityRepository.WritingReferencesAsync(() => db.SaveChangesAsync()));
    }
}

/// <summary>A server with no priorities of the operator's own: each kind has just "Default", and everything gets it.</summary>
[TestFixture]
[Category("Integration")]
public class FreshInstallPriorityTests
{
    private IntegrationTestFixture _fixture = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new IntegrationTestFixture { UseStandardPriorities = false };
        await _fixture.SetupAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _fixture.DisposeAsync();
    }

    [Test]
    public async Task ANewWorkerAndAPlainJob_GetDefault_AndTheJobRunsOnAnyWorker()
    {
        Guid workerId;
        using (var scope = _fixture.Services.CreateScope())
        {
            workerId = (await scope.ServiceProvider.GetRequiredService<WorkerRegistrationService>().RegisterWorkerAsync("fresh-worker")).WorkerId;
        }
        var jobId = await _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = "https://example.invalid/repo.git",
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        var worker = await _fixture.Services.GetRequiredService<WorkerRepository>().GetByIdAsync(workerId);
        var job = await _fixture.GetJobAsync(jobId);
        Assert.That(worker!.Priority.Name, Is.EqualTo("Default"));
        Assert.That(job.Priority.Name, Is.EqualTo("Default"));
        Assert.That(job.MinWorkerPriority, Is.Null);
    }
}
