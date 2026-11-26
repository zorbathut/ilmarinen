using Ilmarinen.Database;
using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// Revoking a worker that is running a job: the server can't know whether the worker stopped the run, so it fails the job as lost rather than leave it Running for a worker that can never come back, or run it twice.
/// </summary>
[TestFixture]
[Category("Integration")]
public class RevokeTests
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
    public async Task RevokingAConnectedWorker_FailsItsRunningJob_AndTellsItToStop()
    {
        var worker = await DirectedWorker.ReadyAsync(_fixture, "revoke-connected", "conn-revoke-connected");
        var jobId = await StartAsync(worker);

        await RevokeAsync(worker);

        var job = await _fixture.GetJobAsync(jobId);
        Assert.That(job.Status, Is.EqualTo(JobStatus.Failed));
        Assert.That(job.CompletedAt, Is.Not.Null);
        Assert.That((await worker.SentAsync()).CancelledJobs, Is.EqualTo(new[] { jobId.ToString() }));
        Assert.That(await _fixture.GetJobLogsAsync(jobId), Does.Contain("[ilmarinen]"), "the log says why the run ended");
    }

    [Test]
    public async Task RevokingADisconnectedWorker_FailsItsRunningJob()
    {
        var worker = await DirectedWorker.ReadyAsync(_fixture, "revoke-away", "conn-revoke-away");
        var jobId = await StartAsync(worker);
        await worker.Hub.OnDisconnectedAsync(null);

        await RevokeAsync(worker);

        Assert.That((await _fixture.GetJobAsync(jobId)).Status, Is.EqualTo(JobStatus.Failed));
    }

    [Test]
    public async Task FailOrphaned_FailsOnlyTheRunningJobsOfWorkersThatAreGone()
    {
        var gone = await DirectedWorker.ReadyAsync(_fixture, "orphan-gone", "conn-orphan-gone");
        var strandedId = await StartAsync(gone);
        var alive = await DirectedWorker.ReadyAsync(_fixture, "orphan-alive", "conn-orphan-alive");
        var aliveJobId = await StartAsync(alive);

        // As a revoke before this was handled, or one the server died in the middle of, left it: the row gone, the job still Running for it.
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
            await db.Workers.Where(w => w.Id == gone.WorkerId).ExecuteDeleteAsync();
        }

        await Scheduler.FailOrphanedAsync();

        Assert.That((await _fixture.GetJobAsync(strandedId)).Status, Is.EqualTo(JobStatus.Failed));
        Assert.That((await _fixture.GetJobAsync(aliveJobId)).Status, Is.EqualTo(JobStatus.Running));
    }

    private JobScheduler Scheduler
    {
        get
        {
            return _fixture.Services.GetRequiredService<JobScheduler>();
        }
    }

    private async Task RevokeAsync(DirectedWorker worker)
    {
        using var scope = _fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WorkerRegistrationService>().RevokeWorkerAsync(worker.WorkerId);
    }

    private async Task<Guid> StartAsync(DirectedWorker worker)
    {
        var jobId = await Scheduler.EnqueueJobAsync(new JobSubmission
        {
            RepoUrl = "/nonexistent/repo",
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });
        var assignment = (await worker.SentAsync()).Assignments.Last(a => a.Id == jobId).AssignmentId;
        await worker.ReportAsync(worker.Running(jobId, assignment));
        Assert.That((await _fixture.GetJobAsync(jobId)).Status, Is.EqualTo(JobStatus.Running));
        return jobId;
    }
}
