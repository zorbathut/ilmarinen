using Ilmarinen.Database;
using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Hubs;
using Ilmarinen.Server.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// Exercises <see cref="JobScheduler"/> and <see cref="WorkerHub"/> against a single
/// connected worker without a real worker process, by driving the same scheduler/hub
/// calls a worker would. The invariant under test: a worker must never have more than
/// one job dispatched to it at once, no matter how readiness signals overlap.
/// </summary>
[TestFixture]
[Category("Integration")]
public class JobSchedulingTests
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

    private static JobSubmission Submission() => new()
    {
        RepoUrl = "https://example.invalid/repo.git",
        Ref = "master",
        ScriptPath = "pipeline.csx",
        GitTokenMode = GitTokenMode.None
    };

    private async Task<Guid> RegisterReadyWorkerAsync(string connectionId)
    {
        using var scope = _fixture.Services.CreateScope();
        var registration = scope.ServiceProvider.GetRequiredService<WorkerRegistrationService>();
        var result = await registration.RegisterWorkerAsync($"sched-test-{Guid.NewGuid():N}");

        var workers = _fixture.Services.GetRequiredService<WorkerRepository>();
        await workers.ConnectAsync(connectionId, result.WorkerId);
        workers.SetReady(connectionId, true);

        return result.WorkerId;
    }

    private async Task<int> RunningJobCountAsync(Guid workerId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        return await db.Jobs.CountAsync(j => j.WorkerId == workerId && j.Status == JobStatus.Running);
    }

    private async Task<JobStatus> JobStatusAsync(Guid jobId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        return (await db.Jobs.FirstAsync(j => j.Id == jobId)).Status;
    }

    private WorkerHub MakeHub(string connectionId) => new(
        _fixture.Services.GetRequiredService<IServiceScopeFactory>(),
        _fixture.Services.GetRequiredService<ServerKeyService>(),
        _fixture.Services.GetRequiredService<UIEventService>(),
        _fixture.Services.GetRequiredService<ILogger<WorkerHub>>())
    {
        Context = new FakeHubCallerContext(connectionId)
    };

    [Test]
    public async Task JobCompletedThenReady_AssignsOnlyOneJob()
    {
        const string conn = "conn-completed-then-ready";
        var workerId = await RegisterReadyWorkerAsync(conn);

        var scheduler = _fixture.Services.GetRequiredService<JobScheduler>();
        var workers = _fixture.Services.GetRequiredService<WorkerRepository>();

        // First job is dispatched immediately to the idle worker.
        var job1 = await scheduler.EnqueueJobAsync(Submission());
        Assert.That(await RunningJobCountAsync(workerId), Is.EqualTo(1), "first job should be assigned");

        // Two more jobs queue up while the worker is busy.
        await scheduler.EnqueueJobAsync(Submission());
        await scheduler.EnqueueJobAsync(Submission());

        // The worker finishes job1. WorkerHub.JobCompleted marks the worker ready and tries to assign; the worker then *also* sends an explicit Ready, which the hub handles the same way. Both signals must not result in two simultaneous jobs.
        await scheduler.CompleteJobAsync(job1, JobStatus.Success);

        workers.SetReady(conn, true);
        await scheduler.TryAssignJobAsync(conn); // JobCompleted path

        workers.SetReady(conn, true);
        await scheduler.TryAssignJobAsync(conn); // redundant Ready path

        Assert.That(await RunningJobCountAsync(workerId), Is.EqualTo(1),
            "a worker must never have more than one job running at once");
    }

    [Test]
    public async Task Ready_WhileWorkerHasRunningJob_DoesNotLeaveWorkerReady()
    {
        const string conn = "conn-ready-while-busy";
        var workerId = await RegisterReadyWorkerAsync(conn);

        var scheduler = _fixture.Services.GetRequiredService<JobScheduler>();
        var workers = _fixture.Services.GetRequiredService<WorkerRepository>();

        await scheduler.EnqueueJobAsync(Submission());
        Assert.That(await RunningJobCountAsync(workerId), Is.EqualTo(1));

        // A stray Ready arrives while the worker is still running its job.
        workers.SetReady(conn, true);
        await scheduler.TryAssignJobAsync(conn);

        Assert.That(workers.GetByConnectionId(conn)!.IsReady, Is.False,
            "a worker with a running job must not be marked ready");

        // The UI-facing readers must agree — a worker shown as Ready while a job runs is an impossible state.
        var view = await workers.GetByIdAsync(workerId);
        Assert.That(view!.IsReady, Is.False);
        Assert.That(view.CurrentJobs, Has.Count.EqualTo(1));

        Assert.That(await RunningJobCountAsync(workerId), Is.EqualTo(1));
    }

    [Test]
    public async Task ConcurrentAssignAttempts_NeverDoubleDispatch()
    {
        const string conn = "conn-concurrent";
        var workerId = await RegisterReadyWorkerAsync(conn);

        var scheduler = _fixture.Services.GetRequiredService<JobScheduler>();
        var workers = _fixture.Services.GetRequiredService<WorkerRepository>();

        // First job is dispatched on enqueue; the rest queue up behind the busy worker.
        var jobIds = new List<Guid>();
        for (var i = 0; i < 6; i++)
            jobIds.Add(await scheduler.EnqueueJobAsync(Submission()));
        Assert.That(await RunningJobCountAsync(workerId), Is.EqualTo(1));

        // The worker finishes. Fire many assignment attempts at once — the overlap produced by JobCompleted, Ready, and EnqueueJob racing on one connection. Only one job may land on the worker.
        await scheduler.CompleteJobAsync(jobIds[0], JobStatus.Success);
        workers.SetReady(conn, true);

        var attempts = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => scheduler.TryAssignJobAsync(conn)));
        await Task.WhenAll(attempts);

        Assert.That(await RunningJobCountAsync(workerId), Is.EqualTo(1),
            "concurrent assignment attempts must not stack jobs on one worker");
    }

    [Test]
    public async Task CancelledQueuedJob_IsNeverDispatched()
    {
        const string conn = "conn-cancelled-queued";
        var workerId = await RegisterReadyWorkerAsync(conn);

        var scheduler = _fixture.Services.GetRequiredService<JobScheduler>();
        var workers = _fixture.Services.GetRequiredService<WorkerRepository>();

        var job1 = await scheduler.EnqueueJobAsync(Submission());
        Assert.That(await RunningJobCountAsync(workerId), Is.EqualTo(1));

        var job2 = await scheduler.EnqueueJobAsync(Submission());
        var job3 = await scheduler.EnqueueJobAsync(Submission());

        // job2 is cancelled while it sits in the queue.
        Assert.That(await scheduler.CancelJobAsync(job2), Is.True);

        // The worker frees up; the dispatcher must skip the cancelled job2 and hand job3 to the worker.
        await scheduler.CompleteJobAsync(job1, JobStatus.Success);
        workers.SetReady(conn, true);
        await scheduler.TryAssignJobAsync(conn);

        Assert.That(await JobStatusAsync(job3), Is.EqualTo(JobStatus.Running),
            "the dispatcher must skip cancelled queue entries and assign the next live job");
        Assert.That(await JobStatusAsync(job2), Is.EqualTo(JobStatus.Cancelled));
    }

    [Test]
    public async Task Reconnect_WorkerLostItsJob_FailsJobAndRefreshesUI()
    {
        const string conn = "conn-reconnect-lost-job";
        var workerId = await RegisterReadyWorkerAsync(conn);

        var scheduler = _fixture.Services.GetRequiredService<JobScheduler>();

        var jobId = await scheduler.EnqueueJobAsync(Submission());
        Assert.That(await RunningJobCountAsync(workerId), Is.EqualTo(1));

        // Subscribe only now, immediately before Reconnect — the enqueue/assign calls above fire OnJobsChanged themselves, and subscribing earlier would let the test pass vacuously.
        var uiEvents = _fixture.Services.GetRequiredService<UIEventService>();
        var jobsChanged = 0;
        Action handler = () => Interlocked.Increment(ref jobsChanged);
        uiEvents.OnJobsChanged += handler;
        try
        {
            // The worker reconnects as a fresh process with no job — the server must fail the orphaned job.
            await MakeHub(conn).Reconnect(new WorkerReconnect { RunningJobId = null });
        }
        finally
        {
            uiEvents.OnJobsChanged -= handler;
        }

        Assert.That(await JobStatusAsync(jobId), Is.EqualTo(JobStatus.Failed),
            "a job the worker lost must be marked failed");
        Assert.That(jobsChanged, Is.GreaterThan(0),
            "failing a lost job must refresh the jobs UI like every other terminal transition");
    }

    [Test]
    public async Task JobCompleted_ForAJobTheWorkerIsNotRunning_IsIgnored()
    {
        const string conn = "conn-stale-completion";
        var workerId = await RegisterReadyWorkerAsync(conn);

        var scheduler = _fixture.Services.GetRequiredService<JobScheduler>();

        // The worker is busy with its real job; a second job sits Queued, unassigned.
        var realJob = await scheduler.EnqueueJobAsync(Submission());
        var queuedJob = await scheduler.EnqueueJobAsync(Submission());
        Assert.That(await RunningJobCountAsync(workerId), Is.EqualTo(1));

        // A confused/stale JobCompleted arrives naming the queued job — one this worker never ran. The hub must not mark it complete or free the busy worker.
        await MakeHub(conn).JobCompleted(queuedJob, new JobResult { Id = queuedJob, Status = JobStatus.Success });

        Assert.That(await JobStatusAsync(queuedJob), Is.EqualTo(JobStatus.Queued),
            "a JobCompleted for a job the worker isn't running must not change that job");
        Assert.That(await JobStatusAsync(realJob), Is.EqualTo(JobStatus.Running));
    }

    private sealed class FakeHubCallerContext(string connectionId) : HubCallerContext
    {
        public override string ConnectionId { get; } = connectionId;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }
}
