using Ilmarinen.Database;
using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.NotificationClient;
using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Hubs;
using Ilmarinen.Server.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
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

    private static JobSubmission Submission(string? minWorkerPriority = null) => new()
    {
        RepoUrl = "https://example.invalid/repo.git",
        Ref = "master",
        ScriptPath = "pipeline.csx",
        GitTokenMode = GitTokenMode.None,
        MinWorkerPriority = minWorkerPriority
    };

    private readonly Dictionary<string, DirectedWorker> _workers = new();

    private async Task<Guid> RegisterReadyWorkerAsync(string connectionId, string priority = StandardPriority.Medium)
    {
        var worker = await DirectedWorker.RegisterAsync(_fixture, $"sched-test-{Guid.NewGuid():N}");
        await _fixture.Services.GetRequiredService<WorkerRepository>().SetPriorityAsync(worker.WorkerId, await _fixture.PriorityIdAsync(PriorityKind.Worker, priority));
        await worker.ConnectAsync(connectionId);
        await worker.ResumeAsync(worker.Idle());
        _workers[connectionId] = worker;
        return worker.WorkerId;
    }

    /// <summary>The worker on this connection reports itself idle and healthy — as it does once a job is done — which dispatches.</summary>
    private Task ReportIdleAsync(string connectionId)
    {
        var worker = _workers[connectionId];
        return worker.ReportAsync(worker.Idle());
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

    [Test]
    public async Task JobFinishedThenIdleReportedTwice_AssignsOnlyOneJob()
    {
        const string conn = "conn-completed-then-ready";
        var workerId = await RegisterReadyWorkerAsync(conn);

        var scheduler = _fixture.Services.GetRequiredService<JobScheduler>();

        // First job is dispatched immediately to the idle worker.
        var job1 = await scheduler.EnqueueJobAsync(Submission());
        Assert.That(await RunningJobCountAsync(workerId), Is.EqualTo(1), "first job should be assigned");

        // Two more jobs queue up while the worker is busy.
        await scheduler.EnqueueJobAsync(Submission());
        await scheduler.EnqueueJobAsync(Submission());

        // The worker finishes job1 and says it is idle, and the same report arrives again. Each dispatches; between them they must not result in two simultaneous jobs.
        await scheduler.CompleteJobAsync(job1, JobStatus.Success);
        await ReportIdleAsync(conn);
        await ReportIdleAsync(conn);

        Assert.That(await RunningJobCountAsync(workerId), Is.EqualTo(1),
            "a worker must never have more than one job running at once");
    }

    [Test]
    public async Task IdleReport_WhileWorkerHasRunningJob_DoesNotMakeItReady()
    {
        const string conn = "conn-ready-while-busy";
        var workerId = await RegisterReadyWorkerAsync(conn);

        var scheduler = _fixture.Services.GetRequiredService<JobScheduler>();
        var workers = _fixture.Services.GetRequiredService<WorkerRepository>();

        await scheduler.EnqueueJobAsync(Submission());
        Assert.That(await RunningJobCountAsync(workerId), Is.EqualTo(1));

        // A second job queues up behind the busy worker, so the dispatcher has something to place and will actually consider it as a candidate.
        var queuedJob = await scheduler.EnqueueJobAsync(Submission());

        // A confused report says the worker is idle while the server has its first job running.
        await ReportIdleAsync(conn);

        // A worker shown as Ready while a job runs is an impossible state.
        var view = await workers.GetByIdAsync(workerId);
        Assert.That(view!.IsReady, Is.False);
        Assert.That(view.CurrentJobs, Has.Count.EqualTo(1));

        Assert.That(await RunningJobCountAsync(workerId), Is.EqualTo(1));
        Assert.That(await JobStatusAsync(queuedJob), Is.EqualTo(JobStatus.Queued),
            "the busy worker must not be handed the queued job");
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

        // The worker finishes. Fire many assignment attempts at once — the overlap produced by outcomes, state reports and submissions racing. Only one job may land on the worker.
        await scheduler.CompleteJobAsync(jobIds[0], JobStatus.Success);
        await ReportIdleAsync(conn);

        var attempts = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => scheduler.DispatchAsync()));
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
        await ReportIdleAsync(conn);

        Assert.That(await JobStatusAsync(job3), Is.EqualTo(JobStatus.Running),
            "the dispatcher must skip cancelled queue entries and assign the next live job");
        Assert.That(await JobStatusAsync(job2), Is.EqualTo(JobStatus.Cancelled));
    }

    public enum TokenDamage
    {
        // Fails authentication exactly as a changed server key does.
        FlippedByte,
        NotBase64,
        TooShort
    }

    [TestCase(TokenDamage.FlippedByte)]
    [TestCase(TokenDamage.NotBase64)]
    [TestCase(TokenDamage.TooShort)]
    public async Task Dispatch_QueuedJobWithUndecryptableToken_FailsItAndServesNextJob(TokenDamage damage)
    {
        var scheduler = _fixture.Services.GetRequiredService<JobScheduler>();

        // Both jobs queue with no worker connected.
        var poisonJob = await scheduler.EnqueueJobAsync(Submission() with { GitTokenMode = GitTokenMode.Explicit, GitToken = "token" });
        var plainJob = await scheduler.EnqueueJobAsync(Submission());

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
            var job = await db.Jobs.FirstAsync(j => j.Id == poisonJob);
            var sealedToken = Convert.FromBase64String(job.EncryptedGitToken!);
            sealedToken[sealedToken.Length / 2] ^= 0xFF;
            job.EncryptedGitToken = damage switch
            {
                TokenDamage.FlippedByte => Convert.ToBase64String(sealedToken),
                TokenDamage.NotBase64 => "not base64!",
                TokenDamage.TooShort => Convert.ToBase64String(sealedToken[..4]),
                _ => throw new ArgumentOutOfRangeException(nameof(damage))
            };
            await db.SaveChangesAsync();
        }

        // Two workers, so the test sees whether a job's failure takes the worker it happened on out of rotation.
        var firstWorker = await RegisterReadyWorkerAsync("conn-undecryptable-token-a");
        var secondWorker = await RegisterReadyWorkerAsync("conn-undecryptable-token-b");
        await scheduler.DispatchAsync();

        Assert.That(await JobStatusAsync(poisonJob), Is.EqualTo(JobStatus.Failed),
            "a job whose token can never be decrypted can never run, so it must fail rather than sit queued");
        Assert.That(await JobStatusAsync(plainJob), Is.EqualTo(JobStatus.Running),
            "the workers are not at fault and must go on to take the next job");
        Assert.That(await RunningJobCountAsync(firstWorker) + await RunningJobCountAsync(secondWorker), Is.EqualTo(1));

        Assert.That(await _workers["conn-undecryptable-token-a"].IsReadyAsync() || await _workers["conn-undecryptable-token-b"].IsReadyAsync(), Is.True,
            "the worker that took no job must still be ready for the next one");
    }

    [Test]
    public async Task Dispatch_JobQueuedBeforeServerRestart_IsDispatchedAfterIt()
    {
        var jobId = await _fixture.Services.GetRequiredService<JobScheduler>().EnqueueJobAsync(Submission());
        Assert.That(await JobStatusAsync(jobId), Is.EqualTo(JobStatus.Queued));

        await _fixture.RestartServerAsync(null);

        var workerId = await RegisterReadyWorkerAsync("conn-after-restart");
        await _fixture.Services.GetRequiredService<JobScheduler>().DispatchAsync();

        Assert.That(await JobStatusAsync(jobId), Is.EqualTo(JobStatus.Running),
            "the queue lives in the database, so a restarted server must still hand out what was queued before");
        Assert.That(await RunningJobCountAsync(workerId), Is.EqualTo(1));
    }

    [Test]
    public async Task Resume_FromANewProcessWithoutTheJob_FailsItAndRefreshesUI()
    {
        const string conn = "conn-reconnect-lost-job";
        var workerId = await RegisterReadyWorkerAsync(conn);

        var scheduler = _fixture.Services.GetRequiredService<JobScheduler>();

        var jobId = await scheduler.EnqueueJobAsync(Submission());
        Assert.That(await RunningJobCountAsync(workerId), Is.EqualTo(1));

        // Subscribe only now, immediately before Resume — the enqueue/assign calls above fire OnJobsChanged themselves, and subscribing earlier would let the test pass vacuously.
        var uiEvents = _fixture.Services.GetRequiredService<UIEventService>();
        var jobsChanged = 0;
        Action handler = () => Interlocked.Increment(ref jobsChanged);
        uiEvents.OnJobsChanged += handler;
        try
        {
            // The worker comes back as a fresh process with no job — it may have run part of it, so the server must fail it.
            var worker = _workers[conn];
            worker.Restart();
            await worker.ConnectAsync("conn-reconnect-lost-job-restarted");
            await worker.ResumeAsync(worker.Idle());
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
    public async Task Outcome_ForAJobTheWorkerIsNotRunning_IsIgnored()
    {
        const string conn = "conn-stale-completion";
        var workerId = await RegisterReadyWorkerAsync(conn);

        var scheduler = _fixture.Services.GetRequiredService<JobScheduler>();

        // The worker is busy with its real job; a second job sits Queued, unassigned.
        var realJob = await scheduler.EnqueueJobAsync(Submission());
        var queuedJob = await scheduler.EnqueueJobAsync(Submission());
        Assert.That(await RunningJobCountAsync(workerId), Is.EqualTo(1));

        // A confused/stale outcome arrives naming the queued job — one this worker never ran. The hub must not mark it complete or free the busy worker.
        await _workers[conn].ReportAsync(DirectedWorker.Completed(queuedJob, Guid.NewGuid(), JobStatus.Success));

        Assert.That(await JobStatusAsync(queuedJob), Is.EqualTo(JobStatus.Queued),
            "an outcome for a job the worker isn't running must not change that job");
        Assert.That(await JobStatusAsync(realJob), Is.EqualTo(JobStatus.Running));
    }

    // Even with the real assignment id, only the worker the job was assigned to can report it.
    [Test]
    public async Task Outcome_FromAnotherWorker_IsIgnored()
    {
        const string busyConn = "conn-busy-owner";
        const string idleConn = "conn-idle-other";
        var owner = await RegisterReadyWorkerAsync(busyConn);

        var scheduler = _fixture.Services.GetRequiredService<JobScheduler>();
        var jobId = await scheduler.EnqueueJobAsync(Submission());
        Assert.That(await RunningJobCountAsync(owner), Is.EqualTo(1));
        await RegisterReadyWorkerAsync(idleConn);

        var assignment = (await _workers[busyConn].SentAsync()).Assignments.Single(a => a.Id == jobId);
        await _workers[idleConn].ReportAsync(DirectedWorker.Completed(jobId, assignment.AssignmentId, JobStatus.Success));

        Assert.That(await JobStatusAsync(jobId), Is.EqualTo(JobStatus.Running), "only the worker running a job can report its result");
    }

    [TestCase(StandardPriority.High, StandardPriority.Low)]
    [TestCase(StandardPriority.Low, StandardPriority.High)]
    public async Task EnqueueJob_ReadyWorkersOfDifferentPriorities_AssignsToHighest(string priorityA, string priorityB)
    {
        // Both cases use the same two connection IDs and swap only the priorities, so the ready-set enumeration order is identical between them. Under first-match selection exactly one case must fail; neither can pass on enumeration luck.
        const string connA = "conn-priority-a";
        const string connB = "conn-priority-b";
        var workerA = await RegisterReadyWorkerAsync(connA, priorityA);
        var workerB = await RegisterReadyWorkerAsync(connB, priorityB);

        var scheduler = _fixture.Services.GetRequiredService<JobScheduler>();
        await scheduler.EnqueueJobAsync(Submission());

        var (winner, loser) = priorityA == StandardPriority.High ? (workerA, workerB) : (workerB, workerA);

        Assert.That(await RunningJobCountAsync(winner), Is.EqualTo(1),
            "the job must go to the highest-priority ready worker");
        Assert.That(await RunningJobCountAsync(loser), Is.EqualTo(0));
    }

    [Test]
    public async Task Dispatch_QueuedJobWithReadyWorkers_AssignsToHighest()
    {
        var scheduler = _fixture.Services.GetRequiredService<JobScheduler>();

        // Both workers are ready, registered lowest-first so a first-match scan would find the Low one.
        var lowWorker = await RegisterReadyWorkerAsync("conn-queued-low", StandardPriority.Low);
        var highWorker = await RegisterReadyWorkerAsync("conn-queued-high", StandardPriority.High);

        // Queued behind the scheduler's back, so the dispatch below is the first to see it with both workers ready (a worker becoming ready dispatches by itself, and would take it first).
        Guid jobId;
        using (var scope = _fixture.Services.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
            jobId = (await jobs.CreateAsync(Submission())).Id;
            await jobs.UpdateStatusAsync(jobId, JobStatus.Queued);
        }

        await scheduler.DispatchAsync();

        // Priority governs an already-queued job, not just one being submitted. (It does not hold a job back for a worker that has yet to become ready — a lone Low worker still takes the job.)
        Assert.That(await RunningJobCountAsync(highWorker), Is.EqualTo(1),
            "a queued job must go to the highest-priority ready worker");
        Assert.That(await RunningJobCountAsync(lowWorker), Is.EqualTo(0));
    }

    [Test]
    public async Task Dispatch_HighestPriorityWorkerIsBusy_FallsThroughToNext()
    {
        const string connHigh = "conn-fallthrough-high";
        var highWorker = await RegisterReadyWorkerAsync(connHigh, StandardPriority.High);

        var scheduler = _fixture.Services.GetRequiredService<JobScheduler>();

        await scheduler.EnqueueJobAsync(Submission());
        Assert.That(await RunningJobCountAsync(highWorker), Is.EqualTo(1));

        // A confused report has the high-priority worker idle while it is still running that job. It sorts first in every scan, so a job that stops at the top candidate would stall forever.
        await ReportIdleAsync(connHigh);

        var lowWorker = await RegisterReadyWorkerAsync("conn-fallthrough-low", StandardPriority.Low);

        var jobId = await scheduler.EnqueueJobAsync(Submission());

        Assert.That(await RunningJobCountAsync(highWorker), Is.EqualTo(1),
            "a worker already running a job must not be handed a second one");
        Assert.That(await RunningJobCountAsync(lowWorker), Is.EqualTo(1),
            "the job must fall through to the next-highest ready worker instead of stalling in the queue");
        Assert.That(await JobStatusAsync(jobId), Is.EqualTo(JobStatus.Running));
        Assert.That(await _workers[connHigh].IsReadyAsync(), Is.False, "a worker with a running job is never ready");
    }

    [TestCase(StandardPriority.Medium, StandardPriority.Medium, true)]
    [TestCase(StandardPriority.Medium, StandardPriority.High, false)]
    [TestCase(StandardPriority.High, StandardPriority.Medium, true)]
    [TestCase(StandardPriority.Low, StandardPriority.Low, true)]
    public async Task EnqueueJob_MinWorkerPriority_GatesOnWorkerPriority(string workerPriority, string minWorkerPriority, bool runs)
    {
        var workerId = await RegisterReadyWorkerAsync("conn-min-gate", workerPriority);

        var scheduler = _fixture.Services.GetRequiredService<JobScheduler>();
        var jobId = await scheduler.EnqueueJobAsync(Submission(minWorkerPriority));

        Assert.That(await JobStatusAsync(jobId), Is.EqualTo(runs ? JobStatus.Running : JobStatus.Queued));
        Assert.That(await RunningJobCountAsync(workerId), Is.EqualTo(runs ? 1 : 0));
    }

    [Test]
    public async Task Dispatch_IneligibleJobAtHead_DoesNotBlockEligibleJobBehindIt()
    {
        var scheduler = _fixture.Services.GetRequiredService<JobScheduler>();

        var highOnlyJob = await scheduler.EnqueueJobAsync(Submission(StandardPriority.High));
        var anyWorkerJob = await scheduler.EnqueueJobAsync(Submission(StandardPriority.Low));

        var workerId = await RegisterReadyWorkerAsync("conn-ineligible-head", StandardPriority.Medium);
        await scheduler.DispatchAsync();

        Assert.That(await JobStatusAsync(anyWorkerJob), Is.EqualTo(JobStatus.Running),
            "a job no ready worker may run must not hold up the jobs queued behind it");
        Assert.That(await JobStatusAsync(highOnlyJob), Is.EqualTo(JobStatus.Queued));
        Assert.That(await RunningJobCountAsync(workerId), Is.EqualTo(1));
    }

    [Test]
    public async Task Dispatch_SeveralEligibleJobs_TakesOldestFirst()
    {
        var scheduler = _fixture.Services.GetRequiredService<JobScheduler>();

        var olderJob = await scheduler.EnqueueJobAsync(Submission(StandardPriority.Low));
        var newerJob = await scheduler.EnqueueJobAsync(Submission(StandardPriority.High));

        await RegisterReadyWorkerAsync("conn-oldest-eligible", StandardPriority.High);
        await scheduler.DispatchAsync();

        // Deliberately first-come first-served among the jobs a worker qualifies for: the High worker does not pass over the older job to favor the one only it could run.
        Assert.That(await JobStatusAsync(olderJob), Is.EqualTo(JobStatus.Running));
        Assert.That(await JobStatusAsync(newerJob), Is.EqualTo(JobStatus.Queued));
    }

    [Test]
    public async Task Dispatch_EligibleWorkerBecomesReady_TakesWaitingJob()
    {
        var mediumWorker = await RegisterReadyWorkerAsync("conn-waiting-medium", StandardPriority.Medium);

        var scheduler = _fixture.Services.GetRequiredService<JobScheduler>();
        var jobId = await scheduler.EnqueueJobAsync(Submission(StandardPriority.High));
        Assert.That(await JobStatusAsync(jobId), Is.EqualTo(JobStatus.Queued));

        var highWorker = await RegisterReadyWorkerAsync("conn-waiting-high", StandardPriority.High);
        await scheduler.DispatchAsync();

        Assert.That(await RunningJobCountAsync(highWorker), Is.EqualTo(1));
        Assert.That(await RunningJobCountAsync(mediumWorker), Is.EqualTo(0));
    }

    [Test]
    public async Task UpdateWorker_RaisingPriority_DispatchesWaitingJob()
    {
        var workerId = await RegisterReadyWorkerAsync("conn-promoted", StandardPriority.Medium);

        var scheduler = _fixture.Services.GetRequiredService<JobScheduler>();
        var jobId = await scheduler.EnqueueJobAsync(Submission(StandardPriority.High));
        Assert.That(await JobStatusAsync(jobId), Is.EqualTo(JobStatus.Queued));

        var response = await _fixture.HttpClient.PutAsJsonAsync($"/api/workers/{workerId}", new WorkerUpdate { Priority = StandardPriority.High });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        Assert.That(await JobStatusAsync(jobId), Is.EqualTo(JobStatus.Running),
            "promoting an idle worker makes it eligible for the waiting job, and nothing else will prompt a dispatch");
        Assert.That(await RunningJobCountAsync(workerId), Is.EqualTo(1));
    }

    private async Task<UnsatisfiableJobsReport> GetUnsatisfiableJobsAsync()
    {
        using var client = new IlmarinenNotificationClient(_fixture.HttpClient.BaseAddress!.ToString(), _fixture.HttpClient);
        return await client.GetUnsatisfiableJobsAsync(CancellationToken.None);
    }

    [Test]
    public async Task GetUnsatisfiableJobs_NoWorkerConnected_ListsEveryQueuedJob()
    {
        var scheduler = _fixture.Services.GetRequiredService<JobScheduler>();
        var anyWorkerJob = await scheduler.EnqueueJobAsync(Submission(StandardPriority.Low));
        var highOnlyJob = await scheduler.EnqueueJobAsync(Submission(StandardPriority.High));

        var report = await GetUnsatisfiableJobsAsync();

        Assert.That(report.HighestAvailableWorkerPriority, Is.Null);
        Assert.That(report.Jobs.Select(j => j.Id), Is.EqualTo(new[] { anyWorkerJob, highOnlyJob }),
            "with no worker available every queued job is stuck, oldest first");
    }

    [Test]
    public async Task GetUnsatisfiableJobs_ListsOnlyJobsAboveTheHighestAvailableWorker()
    {
        await RegisterReadyWorkerAsync("conn-unsatisfiable-busy", StandardPriority.Medium);

        var scheduler = _fixture.Services.GetRequiredService<JobScheduler>();
        var runningJob = await scheduler.EnqueueJobAsync(Submission());
        Assert.That(await JobStatusAsync(runningJob), Is.EqualTo(JobStatus.Running));

        var anyWorkerJob = await scheduler.EnqueueJobAsync(Submission(StandardPriority.Low));
        var highOnlyJob = await scheduler.EnqueueJobAsync(Submission(StandardPriority.High));

        var report = await GetUnsatisfiableJobsAsync();

        // The busy Medium worker will take the Low job once it is free, so only the High job is stuck.
        Assert.That(report.HighestAvailableWorkerPriority?.Name, Is.EqualTo(StandardPriority.Medium));
        Assert.That(report.Jobs.Select(j => j.Id), Is.EqualTo(new[] { highOnlyJob }));
        Assert.That(await JobStatusAsync(anyWorkerJob), Is.EqualTo(JobStatus.Queued));
    }

    [Test]
    public async Task GetUnsatisfiableJobs_IdleReadyWorkerBelowTheMinimum_CountsAsAvailable()
    {
        await RegisterReadyWorkerAsync("conn-unsatisfiable-idle", StandardPriority.Medium);

        var scheduler = _fixture.Services.GetRequiredService<JobScheduler>();
        var highOnlyJob = await scheduler.EnqueueJobAsync(Submission(StandardPriority.High));

        var report = await GetUnsatisfiableJobsAsync();

        Assert.That(report.HighestAvailableWorkerPriority?.Name, Is.EqualTo(StandardPriority.Medium),
            "an idle worker is available even when it can't take the stuck job; the report must not claim no worker is available");
        Assert.That(report.Jobs.Select(j => j.Id), Is.EqualTo(new[] { highOnlyJob }));
    }

    [Test]
    public async Task GetUnsatisfiableJobs_PipelineJob_CarriesItsPipelineName()
    {
        var repository = await _fixture.CreateRepositoryAsync(new RepositorySubmission
        {
            Name = "unsatisfiable-repo",
            RepoUrl = "https://example.invalid/repo.git"
        });
        var pipeline = await _fixture.CreatePipelineAsync(new PipelineSubmission
        {
            Name = "unsatisfiable-pipeline",
            RepositoryId = repository.Id,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        await _fixture.Services.GetRequiredService<JobScheduler>().EnqueueJobAsync(new JobSubmission
        {
            PipelineId = pipeline.Id,
            GitTokenMode = GitTokenMode.Inherit
        });

        var report = await GetUnsatisfiableJobsAsync();

        Assert.That(report.Jobs.Single().PipelineName, Is.EqualTo(pipeline.Name));
    }

    [Test]
    public async Task GetUnsatisfiableJobs_ConnectedWorkerThatIsNeitherReadyNorBusy_DoesNotCount()
    {
        const string conn = "conn-unsatisfiable-not-ready";
        await RegisterReadyWorkerAsync(conn, StandardPriority.High);
        await _workers[conn].ReportAsync(_workers[conn].Idle(DiagnosticStatus.Unhealthy));

        var scheduler = _fixture.Services.GetRequiredService<JobScheduler>();
        var jobId = await scheduler.EnqueueJobAsync(Submission(StandardPriority.High));

        var report = await GetUnsatisfiableJobsAsync();

        Assert.That(report.HighestAvailableWorkerPriority, Is.Null,
            "a connected worker that will never be handed work must not count as available");
        Assert.That(report.Jobs.Select(j => j.Id), Is.EqualTo(new[] { jobId }));
    }

    [Test]
    public async Task GetUnsatisfiableJobs_OnlyWorkerDisconnects_QueuedJobsBecomeUnsatisfiable()
    {
        const string conn = "conn-unsatisfiable-disconnect";
        await RegisterReadyWorkerAsync(conn, StandardPriority.Low);

        var scheduler = _fixture.Services.GetRequiredService<JobScheduler>();
        await scheduler.EnqueueJobAsync(Submission());
        var queuedJob = await scheduler.EnqueueJobAsync(Submission());

        Assert.That((await GetUnsatisfiableJobsAsync()).Jobs, Is.Empty);

        await _workers[conn].Hub.OnDisconnectedAsync(null);

        Assert.That((await GetUnsatisfiableJobsAsync()).Jobs.Select(j => j.Id), Is.EqualTo(new[] { queuedJob }));
    }
}
