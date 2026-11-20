using Ilmarinen.Database;
using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.NotificationClient;
using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>A worker that says it is taking no new jobs is sent none until it says otherwise; what it is running carries on.</summary>
[TestFixture]
[Category("Integration")]
public class WorkerHoldTests
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

    private JobScheduler Scheduler
    {
        get { return _fixture.Services.GetRequiredService<JobScheduler>(); }
    }

    private static JobSubmission Submission()
    {
        return new JobSubmission
        {
            RepoUrl = "https://example.invalid/repo.git",
            Ref = "master",
            ScriptPath = "pipeline.csx",
            GitTokenMode = GitTokenMode.None
        };
    }

    private async Task<JobStatus> JobStatusAsync(Guid jobId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        return (await db.Jobs.FirstAsync(j => j.Id == jobId)).Status;
    }

    [Test]
    public async Task AHeldWorker_IsSentNoJob_UntilItReleases()
    {
        var worker = await DirectedWorker.ReadyAsync(_fixture, "hold-idle", "conn-hold-idle");
        await worker.ReportAsync(worker.Idle() with { Hold = WorkerHold.ShuttingDown });
        Assert.That(await worker.IsReadyAsync(), Is.False);

        var jobId = await Scheduler.EnqueueJobAsync(Submission());

        Assert.That(await JobStatusAsync(jobId), Is.EqualTo(JobStatus.Queued));
        Assert.That((await worker.SentAsync()).Assignments, Is.Empty);

        await worker.ReportAsync(worker.Idle());

        Assert.That(await JobStatusAsync(jobId), Is.EqualTo(JobStatus.Running), "the release is a state report, and every report dispatches");
        Assert.That((await worker.SentAsync()).Assignments.Select(a => a.Id), Is.EqualTo(new[] { jobId }));
    }

    [Test]
    public async Task AWorkerHeldMidJob_FinishesIt_ButTakesNoNextJob()
    {
        var worker = await DirectedWorker.ReadyAsync(_fixture, "hold-mid-job", "conn-hold-mid-job");
        var first = await Scheduler.EnqueueJobAsync(Submission());
        var assignment = (await worker.SentAsync()).Assignments.Single();
        await worker.ReportAsync(worker.Running(first, assignment.AssignmentId) with { Hold = WorkerHold.ShuttingDown });
        var second = await Scheduler.EnqueueJobAsync(Submission());

        await worker.ReportAsync(DirectedWorker.Completed(first, assignment.AssignmentId, JobStatus.Success));
        await worker.ReportAsync(worker.Idle() with { Hold = WorkerHold.ShuttingDown });

        Assert.That(await JobStatusAsync(first), Is.EqualTo(JobStatus.Success));
        Assert.That(await JobStatusAsync(second), Is.EqualTo(JobStatus.Queued));
        Assert.That((await worker.SentAsync()).Assignments, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task AHold_ResumedWith_IsHonoured()
    {
        var worker = await DirectedWorker.RegisterAsync(_fixture, "hold-resume");
        var jobId = await Scheduler.EnqueueJobAsync(Submission());

        await worker.ConnectAsync("conn-hold-resume");
        await worker.ResumeAsync(worker.Idle() with { Hold = WorkerHold.ShuttingDown });

        Assert.That(await JobStatusAsync(jobId), Is.EqualTo(JobStatus.Queued));
        Assert.That(await worker.IsReadyAsync(), Is.False);
    }

    [Test]
    public async Task AnOverTakenHoldReport_DoesNotHoldTheWorkerAgain()
    {
        var worker = await DirectedWorker.ReadyAsync(_fixture, "hold-stale", "conn-hold-stale");
        var held = worker.Idle() with { Hold = WorkerHold.ShuttingDown };
        var released = worker.Idle();

        await worker.ReportAsync(released);
        await worker.ReportAsync(held);

        Assert.That(await worker.IsReadyAsync(), Is.True, "the release came later, by its seq");
    }

    private async Task<UnsatisfiableJobsReport> GetUnsatisfiableJobsAsync()
    {
        using var client = new IlmarinenNotificationClient(_fixture.HttpClient.BaseAddress!.ToString(), _fixture.HttpClient);
        return await client.GetUnsatisfiableJobsAsync(CancellationToken.None);
    }

    // Busy, so readiness alone wouldn't drop it: a worker running a job normally counts as available.
    [Test]
    public async Task ABusyWorkerInUse_IsNotAvailable_AndTheStuckReportNamesIt()
    {
        var worker = await DirectedWorker.ReadyAsync(_fixture, "hold-in-use-busy", "conn-hold-in-use-busy");
        var running = await Scheduler.EnqueueJobAsync(Submission());
        var assignment = (await worker.SentAsync()).Assignments.Single();
        await worker.ReportAsync(worker.Running(running, assignment.AssignmentId) with { Hold = WorkerHold.InUse });
        var queued = await Scheduler.EnqueueJobAsync(Submission());

        var report = await GetUnsatisfiableJobsAsync();

        Assert.That(report.HighestAvailableWorkerPriority, Is.Null);
        Assert.That(report.Jobs.Select(j => j.Id), Is.EqualTo(new[] { queued }));
        Assert.That(report.InUseWorkers, Is.EqualTo(new[] { "hold-in-use-busy" }));
        Assert.That(report.PausedWorkers, Is.Empty);
    }

    [Test]
    public async Task TheStuckReport_NamesAnIdleWorkerInUse_ButNotOneStoppingOrUnhealthy()
    {
        var inUse = await DirectedWorker.ReadyAsync(_fixture, "hold-report-in-use", "conn-hold-report-in-use");
        await inUse.ReportAsync(inUse.Idle() with { Hold = WorkerHold.InUse });
        var stopping = await DirectedWorker.ReadyAsync(_fixture, "hold-report-stopping", "conn-hold-report-stopping");
        await stopping.ReportAsync(stopping.Idle() with { Hold = WorkerHold.ShuttingDown });
        var unhealthy = await DirectedWorker.ReadyAsync(_fixture, "hold-report-unhealthy", "conn-hold-report-unhealthy");
        await unhealthy.ReportAsync(unhealthy.Idle(DiagnosticStatus.Unhealthy) with { Hold = WorkerHold.InUse });

        await Scheduler.EnqueueJobAsync(Submission());

        var report = await GetUnsatisfiableJobsAsync();
        Assert.That(report.Jobs, Has.Count.EqualTo(1));
        Assert.That(report.InUseWorkers, Is.EqualTo(new[] { "hold-report-in-use" }), "letting it be wouldn't free the other two");
    }

    [Test]
    public async Task AWorkerPausedAndInUse_IsNamedAsBoth()
    {
        var worker = await DirectedWorker.ReadyAsync(_fixture, "hold-paused-in-use", "conn-hold-paused-in-use");
        await worker.ReportAsync(worker.Idle() with { Hold = WorkerHold.InUse });
        await Scheduler.SetWorkerPausedAsync(worker.WorkerId, true);
        await Scheduler.EnqueueJobAsync(Submission());

        var report = await GetUnsatisfiableJobsAsync();

        Assert.That(report.PausedWorkers, Is.EqualTo(new[] { "hold-paused-in-use" }));
        Assert.That(report.InUseWorkers, Is.EqualTo(new[] { "hold-paused-in-use" }));
    }

    [Test]
    public async Task APausedWorkerThatIsStopping_IsNotNamedAsPaused_ForUnpausingWouldntFreeIt()
    {
        var worker = await DirectedWorker.ReadyAsync(_fixture, "hold-paused-stopping", "conn-hold-paused-stopping");
        await worker.ReportAsync(worker.Idle() with { Hold = WorkerHold.ShuttingDown });
        await Scheduler.SetWorkerPausedAsync(worker.WorkerId, true);
        await Scheduler.EnqueueJobAsync(Submission());

        var report = await GetUnsatisfiableJobsAsync();

        Assert.That(report.Jobs, Has.Count.EqualTo(1));
        Assert.That(report.PausedWorkers, Is.Empty);
        Assert.That(report.InUseWorkers, Is.Empty);
    }
}
