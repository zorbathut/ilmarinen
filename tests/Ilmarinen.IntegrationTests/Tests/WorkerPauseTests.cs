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
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>An operator's pause: the worker is sent no new jobs, whether or not it is connected, until it is unpaused; what it is running carries on.</summary>
[TestFixture]
[Category("Integration")]
public class WorkerPauseTests
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

    private static JobSubmission Submission(string minWorkerPriority = StandardPriority.Low)
    {
        return new JobSubmission
        {
            RepoUrl = "https://example.invalid/repo.git",
            Ref = "master",
            ScriptPath = "pipeline.csx",
            GitTokenMode = GitTokenMode.None,
            MinWorkerPriority = minWorkerPriority
        };
    }

    private async Task<DirectedWorker> ReadyWorkerAsync(string connectionId, string priority = StandardPriority.Medium)
    {
        var worker = await DirectedWorker.RegisterAsync(_fixture, $"pause-{connectionId}");
        await _fixture.Services.GetRequiredService<WorkerRepository>().SetPriorityAsync(worker.WorkerId, await _fixture.PriorityIdAsync(PriorityKind.Worker, priority));
        await worker.ConnectAsync(connectionId);
        await worker.ResumeAsync(worker.Idle());
        return worker;
    }

    private async Task<JobStatus> JobStatusAsync(Guid jobId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        return (await db.Jobs.FirstAsync(j => j.Id == jobId)).Status;
    }

    private async Task<Guid?> JobWorkerAsync(Guid jobId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        return (await db.Jobs.FirstAsync(j => j.Id == jobId)).WorkerId;
    }

    private async Task<UnsatisfiableJobsReport> GetUnsatisfiableJobsAsync()
    {
        using var client = new IlmarinenNotificationClient(_fixture.HttpClient.BaseAddress!.ToString(), _fixture.HttpClient);
        return await client.GetUnsatisfiableJobsAsync(CancellationToken.None);
    }

    [Test]
    public async Task APausedWorker_IsSentNoJob_UntilUnpaused()
    {
        var worker = await ReadyWorkerAsync("conn-pause-ready");
        Assert.That(await Scheduler.SetWorkerPausedAsync(worker.WorkerId, true), Is.True);
        Assert.That(await worker.IsReadyAsync(), Is.False);

        var jobId = await Scheduler.EnqueueJobAsync(Submission());

        Assert.That(await JobStatusAsync(jobId), Is.EqualTo(JobStatus.Queued));
        Assert.That((await worker.SentAsync()).Assignments, Is.Empty);

        Assert.That(await Scheduler.SetWorkerPausedAsync(worker.WorkerId, false), Is.True);

        Assert.That(await JobStatusAsync(jobId), Is.EqualTo(JobStatus.Running), "unpausing dispatches; nothing else would");
        Assert.That(await JobWorkerAsync(jobId), Is.EqualTo(worker.WorkerId));
        Assert.That((await worker.SentAsync()).Assignments.Select(a => a.Id), Is.EqualTo(new[] { jobId }));
    }

    [Test]
    public async Task AWorkerPausedWhileOffline_IsSentNoJobWhenItConnects()
    {
        var worker = await DirectedWorker.RegisterAsync(_fixture, "pause-offline");
        Assert.That(await Scheduler.SetWorkerPausedAsync(worker.WorkerId, true), Is.True);
        var jobId = await Scheduler.EnqueueJobAsync(Submission());

        await worker.ConnectAsync("conn-pause-offline");
        await worker.ResumeAsync(worker.Idle());

        Assert.That(await JobStatusAsync(jobId), Is.EqualTo(JobStatus.Queued));
        Assert.That((await worker.SentAsync()).Assignments, Is.Empty);
        Assert.That(await worker.IsReadyAsync(), Is.False);
    }

    [Test]
    public async Task AWorkerPausedMidJob_FinishesIt_ButTakesNoNextJob()
    {
        var worker = await ReadyWorkerAsync("conn-pause-mid-job");
        var first = await Scheduler.EnqueueJobAsync(Submission());
        var assignment = (await worker.SentAsync()).Assignments.Single();
        await worker.ReportAsync(worker.Running(first, assignment.AssignmentId));

        Assert.That(await Scheduler.SetWorkerPausedAsync(worker.WorkerId, true), Is.True);
        var second = await Scheduler.EnqueueJobAsync(Submission());

        await worker.ReportAsync(DirectedWorker.Completed(first, assignment.AssignmentId, JobStatus.Success));
        await worker.ReportAsync(worker.Idle());

        Assert.That(await JobStatusAsync(first), Is.EqualTo(JobStatus.Success), "a pause stops new work, not the job in hand");
        Assert.That(await JobStatusAsync(second), Is.EqualTo(JobStatus.Queued));
        Assert.That((await worker.SentAsync()).Assignments, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task APausedHighWorker_IsPassedOverForAReadyLowOne()
    {
        var high = await ReadyWorkerAsync("conn-pause-high", StandardPriority.High);
        var low = await ReadyWorkerAsync("conn-pause-low", StandardPriority.Low);
        Assert.That(await Scheduler.SetWorkerPausedAsync(high.WorkerId, true), Is.True);

        var jobId = await Scheduler.EnqueueJobAsync(Submission());

        Assert.That(await JobWorkerAsync(jobId), Is.EqualTo(low.WorkerId));
        Assert.That((await high.SentAsync()).Assignments, Is.Empty);
    }

    [Test]
    public async Task APause_SurvivesAServerRestart()
    {
        var worker = await DirectedWorker.RegisterAsync(_fixture, "pause-restart");
        await Scheduler.SetWorkerPausedAsync(worker.WorkerId, true);

        await _fixture.RestartServerAsync(null);

        var jobId = await Scheduler.EnqueueJobAsync(Submission());
        await worker.ConnectAsync("conn-pause-restart");
        await worker.ResumeAsync(worker.Idle());

        Assert.That(await JobStatusAsync(jobId), Is.EqualTo(JobStatus.Queued));
        Assert.That(await worker.IsReadyAsync(), Is.False);
        var view = await _fixture.Services.GetRequiredService<WorkerRepository>().GetByIdAsync(worker.WorkerId);
        Assert.That(view!.IsPaused, Is.True);
    }

    // Busy, so readiness alone wouldn't drop it: running a job normally still counts as available.
    [Test]
    public async Task ABusyPausedWorker_IsNotAvailable()
    {
        var worker = await ReadyWorkerAsync("conn-pause-busy");
        var running = await Scheduler.EnqueueJobAsync(Submission());
        Assert.That(await JobStatusAsync(running), Is.EqualTo(JobStatus.Running));
        await Scheduler.SetWorkerPausedAsync(worker.WorkerId, true);
        var queued = await Scheduler.EnqueueJobAsync(Submission());

        var report = await GetUnsatisfiableJobsAsync();
        Assert.That(report.HighestAvailableWorkerPriority, Is.Null);
        Assert.That(report.Jobs.Select(j => j.Id), Is.EqualTo(new[] { queued }));
        Assert.That(report.PausedWorkers, Is.EqualTo(new[] { "pause-conn-pause-busy" }));

        var estimate = await _fixture.HttpClient.GetFromJsonAsync<QueueEstimate>("/api/jobs/queue/estimate");
        Assert.That(estimate!.AvailableWorkers, Is.Zero);
        Assert.That(estimate.RunningJobs, Is.EqualTo(1));
        Assert.That(estimate.UnsatisfiableJobs, Is.EqualTo(1));
    }

    [Test]
    public async Task TheStuckReport_NamesOnlyPausedWorkersThatCouldTakeTheJobs()
    {
        var high = await ReadyWorkerAsync("conn-pause-report-high", StandardPriority.High);
        var low = await ReadyWorkerAsync("conn-pause-report-low", StandardPriority.Low);
        var pausedLow = await ReadyWorkerAsync("conn-pause-report-paused-low", StandardPriority.Low);
        Assert.That(await Scheduler.SetWorkerPausedAsync(high.WorkerId, true), Is.True);
        Assert.That(await Scheduler.SetWorkerPausedAsync(pausedLow.WorkerId, true), Is.True);

        var jobId = await Scheduler.EnqueueJobAsync(Submission(StandardPriority.High));

        var report = await GetUnsatisfiableJobsAsync();
        Assert.That(report.HighestAvailableWorkerPriority?.Name, Is.EqualTo(StandardPriority.Low));
        Assert.That(report.Jobs.Select(j => j.Id), Is.EqualTo(new[] { jobId }));
        Assert.That(report.PausedWorkers, Is.EqualTo(new[] { "pause-conn-pause-report-high" }), "the paused Low worker couldn't take it either way");
        Assert.That((await low.SentAsync()).Assignments, Is.Empty);
    }

    // Unpausing either of these wouldn't free the job: one is unhealthy, the other isn't connected.
    [Test]
    public async Task TheStuckReport_NamesNoPausedWorkerThatUnpausingWouldntFree()
    {
        var unhealthy = await DirectedWorker.RegisterAsync(_fixture, "pause-unhealthy");
        await unhealthy.ConnectAsync("conn-pause-unhealthy");
        await unhealthy.ResumeAsync(unhealthy.Idle(DiagnosticStatus.Unhealthy));
        var offline = await DirectedWorker.RegisterAsync(_fixture, "pause-offline-report");
        Assert.That(await Scheduler.SetWorkerPausedAsync(unhealthy.WorkerId, true), Is.True);
        Assert.That(await Scheduler.SetWorkerPausedAsync(offline.WorkerId, true), Is.True);

        var jobId = await Scheduler.EnqueueJobAsync(Submission());

        var report = await GetUnsatisfiableJobsAsync();
        Assert.That(report.Jobs.Select(j => j.Id), Is.EqualTo(new[] { jobId }));
        Assert.That(report.PausedWorkers, Is.Empty);
    }

    [Test]
    public async Task NothingStuck_NamesNoPausedWorker()
    {
        var worker = await ReadyWorkerAsync("conn-pause-nothing-stuck");
        await Scheduler.SetWorkerPausedAsync(worker.WorkerId, true);

        var report = await GetUnsatisfiableJobsAsync();

        Assert.That(report.Jobs, Is.Empty);
        Assert.That(report.PausedWorkers, Is.Empty);
    }

    [Test]
    public async Task Pausing_AnUnknownWorkerFails_AndPausingTwiceIsHarmless()
    {
        Assert.That(await Scheduler.SetWorkerPausedAsync(Guid.NewGuid(), true), Is.False);

        var worker = await ReadyWorkerAsync("conn-pause-twice");
        Assert.That(await Scheduler.SetWorkerPausedAsync(worker.WorkerId, true), Is.True);
        Assert.That(await Scheduler.SetWorkerPausedAsync(worker.WorkerId, true), Is.True);
        Assert.That(await Scheduler.SetWorkerPausedAsync(worker.WorkerId, false), Is.True);
        Assert.That(await worker.IsReadyAsync(), Is.True);
    }
}
