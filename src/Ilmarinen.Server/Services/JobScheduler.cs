using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Services.Wake;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Ilmarinen.Server.Services;

/// <summary>The answer to a punt: what became of it, where the job would go, and whether its worker could be told at once.</summary>
public record PuntAnswer(PuntStatus Status, PuntForecastInfo? Forecast, bool WorkerConnected);

public class JobScheduler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WorkerMessenger _messenger;
    private readonly WorkerSessions _sessions;
    private readonly UIEventService _uiEvents;
    private readonly LogStreamService _logStream;
    private readonly WorkerWaker _waker;
    private readonly DispatchHold _hold;
    private readonly ILogger<JobScheduler> _logger;

    // Serializes dispatch: which job goes next, and to which worker. Taken before a worker's session lock (WorkerSessions), never inside one, so a hub handler holding a worker's lock dispatches only after releasing it.
    private readonly SemaphoreSlim _assignLock = new(1, 1);

    // A cancel and the worker's own report of the same job complete concurrently, and each creates notifications only where the other hasn't left one pending; serialized, each sees what the other did. The innermost lock: taken inside _assignLock and inside session locks, so nothing may take either while holding this.
    private readonly SemaphoreSlim _notificationLock = new(1, 1);

    public JobScheduler(
        IServiceScopeFactory scopeFactory,
        WorkerMessenger messenger,
        WorkerSessions sessions,
        UIEventService uiEvents,
        LogStreamService logStream,
        WorkerWaker waker,
        DispatchHold hold,
        ILogger<JobScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _messenger = messenger;
        _sessions = sessions;
        _uiEvents = uiEvents;
        _logStream = logStream;
        _waker = waker;
        _hold = hold;
        _logger = logger;
    }

    public async Task<Guid> EnqueueJobAsync(JobSubmission submission)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();

        var info = await jobs.CreateAsync(submission);
        await jobs.UpdateStatusAsync(info.Id, JobStatus.Queued);
        _uiEvents.NotifyJobsChanged();

        await DispatchAsync();

        return info.Id;
    }

    /// <summary>
    /// Hands queued jobs to ready workers: each queued job in dispatch order (highest job priority, then oldest) goes to the highest-priority ready worker that qualifies for it, preferring one that holds its workspace (<see cref="DispatchPlan"/>). Nothing running is ever displaced for a higher-priority job. Called whenever readiness or the queue may have changed — a job is submitted or handed back, a worker resumes or reports a new state, a worker's priority changes or its mode stops holding it back — and keeps going until no ready worker will take another job, so a queued job never sits behind an idle worker that meets its minimum priority.
    ///
    /// The matching is greedy: a job offered first takes its best worker even when a later job could only have run there, and then waits.
    ///
    /// While the fleet is draining (<see cref="DispatchHold"/>) nothing is handed out, and clearing the drain dispatches.
    /// </summary>
    public async Task DispatchAsync()
    {
        await _assignLock.WaitAsync();
        try
        {
            if (_hold.Held)
            {
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
            var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

            // One pass is enough: each worker takes at most one job, and after that it is busy. A worker that frees up meanwhile, or a job submitted meanwhile, raises its own DispatchAsync, which is waiting on this lock.
            var free = (await workers.GetReadyWorkersByPriorityAsync()).ToList();
            if (free.Count == 0)
            {
                return;
            }

            foreach (var queued in await jobs.GetQueuedForDispatchAsync(workers.GetFleetDisks()))
            {
                var (job, wanted) = (queued.Row, queued.Dispatch);
                if (free.Count == 0)
                {
                    return;
                }

                while (DispatchPlan.Pick(wanted, free) is { Worker: var worker, Fit: var fit })
                {
                    // The list was a snapshot; under the worker's lock nothing about it can change while it is checked and handed a job. One no longer ready is out of this pass, and the job goes to the next best.
                    using (await _sessions.LockAsync(worker.WorkerId))
                    {
                        var session = _sessions.ByWorker(worker.WorkerId);
                        if (session == null || await workers.CheckReadyAsync(session) is not { Ready: true } check)
                        {
                            free.Remove(worker);
                            continue;
                        }

                        if (await TryAssignAsync(jobs, session, job.Id, wanted, fit, check.TakeWhileInUse))
                        {
                            free.Remove(worker);
                        }
                        break;
                    }
                }
            }
        }
        finally
        {
            _assignLock.Release();
        }
    }

    /// <summary>
    /// Hands this queued job to the worker. Returns false, leaving the worker free, when the job can't be handed out after all: it was cancelled meanwhile, or its git token can no longer be decrypted (and it has been failed). Caller holds _assignLock and the worker's session lock, and has confirmed it is ready.
    /// </summary>
    private async Task<bool> TryAssignAsync(JobRepository jobs, WorkerSession session, Guid jobId, DispatchJob wanted, DiskFit fit, bool takeWhileInUse)
    {
        var assignmentId = Guid.NewGuid();
        JobAssignment assignment;
        try
        {
            assignment = await jobs.GetAssignmentAsync(jobId, assignmentId) with { PredictedWorkspace = wanted.Workspace, DiskNeededBytes = fit.NeededBytes, StartFresh = fit.StartFresh, DiskNeededToReuseBytes = wanted.Need.ReuseBytes, TakeWhileInUse = takeWhileInUse };
        }
        catch (CryptographicException ex)
        {
            // The stored git token can't be decrypted, typically because the server key changed while the job was queued. No worker can ever run this job, and it is no fault of this one.
            _logger.LogError(ex, "Job {JobId} has a git token that can no longer be decrypted; failing it", jobId);
            await CompleteJobAsync(jobId, JobStatus.Failed);
            return false;
        }

        // Gate the assignment on the status write: a refused write means the job went terminal (cancelled) while queued, so skip it. A cancel that lands after this write sends its CancelJob under this worker's lock too, so it reaches the worker after the assignment, never before.
        if (!await jobs.AssignAsync(jobId, session.WorkerId, assignmentId, session.InstanceId))
        {
            _logger.LogInformation("Skipping queued job {JobId}: cancelled before it could be assigned", jobId);
            return false;
        }

        _uiEvents.NotifyJobsChanged();

        // Queued, not awaited: one unreachable worker must not hold up the jobs the rest of the fleet could take. If the send fails, the job is already marked Running against the worker, and its next Resume requeues it as never delivered.
        _messenger.Send(session.ConnectionId, $"job {jobId}", client => client.AssignJob(assignment));
        return true;
    }

    /// <summary>
    /// Changes a worker's priority, then dispatches: a promoted worker may now meet the minimum of a job that is waiting. Returns false if the worker doesn't exist.
    /// </summary>
    public async Task<bool> SetWorkerPriorityAsync(Guid workerId, Guid priorityId)
    {
        using var scope = _scopeFactory.CreateScope();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

        // Under the dispatch lock, so a demotion can't land between a dispatch reading this worker's priority and handing it a job only the old priority qualified it for.
        var updated = await UnderDispatchLockAsync(() => workers.SetPriorityAsync(workerId, priorityId));

        if (!updated)
        {
            return false;
        }

        _uiEvents.NotifyWorkersChanged();
        await DispatchAsync();
        return true;
    }

    /// <summary>
    /// Makes a change to the lists of priorities, then dispatches: reordering or deleting one changes which workers qualify for which jobs, and the order jobs go in. Every change to the lists goes through here.
    /// </summary>
    public async Task<T> ChangePrioritiesAsync<T>(Func<PriorityRepository, Task<T>> change)
    {
        using var scope = _scopeFactory.CreateScope();
        var priorities = scope.ServiceProvider.GetRequiredService<PriorityRepository>();

        var result = await UnderDispatchLockAsync(() => change(priorities));

        // Names and order show on the workers' and jobs' pages too.
        _uiEvents.NotifyPrioritiesChanged();
        _uiEvents.NotifyWorkersChanged();
        _uiEvents.NotifyJobsChanged();
        await DispatchAsync();
        return result;
    }

    /// <summary>
    /// Changes a queued job's priorities, then dispatches: a lowered minimum may let an idle worker take it, and a raised priority moves it up the queue. Refused for a job no longer queued.
    /// </summary>
    public async Task<JobUpdateResult> UpdateQueuedJobAsync(Guid jobId, JobUpdate update)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();

        // Under the dispatch lock, so a raised minimum can't land between a dispatch reading the queue and handing this job to a worker only the old minimum allowed.
        var result = await UnderDispatchLockAsync(() => jobs.UpdateQueuedAsync(jobId, update));

        if (result.Status == JobUpdateStatus.Updated)
        {
            _uiEvents.NotifyJobsChanged();
            await DispatchAsync();
        }
        return result;
    }

    /// <summary>Runs a change to what dispatch decides from, so that no dispatch is part-way through deciding from the old values when it lands.</summary>
    private async Task<T> UnderDispatchLockAsync<T>(Func<Task<T>> change)
    {
        await _assignLock.WaitAsync();
        try
        {
            return await change();
        }
        finally
        {
            _assignLock.Release();
        }
    }

    /// <summary>
    /// Holds or releases dispatch for the whole fleet (<see cref="DispatchHold"/>); releasing it dispatches.
    /// </summary>
    public async Task SetDrainingAsync(bool draining)
    {
        // Under the dispatch lock, so once a drain begins no dispatch already under way can still hand out a job.
        await UnderDispatchLockAsync(() =>
        {
            _hold.Set(draining);
            return Task.FromResult(true);
        });
        if (draining)
        {
            _logger.LogInformation("Draining: dispatch held until released or the server restarts");
        }
        else
        {
            _logger.LogInformation("Drain released: dispatching again");
        }

        _uiEvents.NotifyWorkersChanged();
        if (!draining)
        {
            await DispatchAsync();
        }
    }

    public async Task<DrainStatus> GetDrainStatusAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
        return new DrainStatus { Draining = _hold.Held, Running = await jobs.GetRunningWithWorkersAsync() };
    }

    /// <summary>
    /// Sets what a worker does about new jobs; any mode but Paused dispatches, since the worker may be ready for a job that is waiting. A paused worker is sent no new jobs, connected or not; what it is running carries on. Returns false if the worker doesn't exist.
    /// </summary>
    public async Task<bool> SetWorkerModeAsync(Guid workerId, WorkerMode mode)
    {
        using var scope = _scopeFactory.CreateScope();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

        // Under the dispatch lock, so once the change returns no dispatch can still be acting on the old mode: none sends a paused worker a job, nor tells a worker set back to Normal to take one while in use.
        var updated = await UnderDispatchLockAsync(() => workers.SetModeAsync(workerId, mode));

        if (!updated)
        {
            return false;
        }

        _uiEvents.NotifyWorkersChanged();
        if (mode != WorkerMode.Paused)
        {
            await DispatchAsync();
        }
        return true;
    }

    /// <summary>
    /// Queued jobs no available worker can take, with the highest priority on offer. A sleeping worker that can still be woken counts as available here, and only here (the queue forecast assumes no wake delay): its jobs aren't stuck until waking it has failed. Taken without _assignLock: it is a read-only snapshot, and a momentary disagreement with an in-flight dispatch clears on the next read. That includes the priorities being reordered or deleted between the reads here: ranks are compared as each read found them.
    /// </summary>
    public async Task<UnsatisfiableJobsReport> GetUnsatisfiableJobsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();
        var priorities = scope.ServiceProvider.GetRequiredService<PriorityRepository>();

        var availability = await workers.GetAvailabilityAsync();
        var (wakeable, failedToWake) = await _waker.GetSleepersAsync();
        var onOffer = availability.Available.Select(w => (Id: w.WorkerId, w.Rank)).Concat(wakeable.Select(w => (w.Id, w.Rank))).ToList();
        int? highestAvailableRank = onOffer.Count == 0 ? null : onOffer.Max(w => w.Rank);
        var stuck = await jobs.GetUnsatisfiableJobsAsync(highestAvailableRank);
        var noRoom = await GetNoRoomJobsAsync(jobs, workers, onOffer, stuck.Select(j => j.Id).ToHashSet());
        var workerPriorities = await priorities.GetAllAsync(PriorityKind.Worker);
        var rankById = workerPriorities.ToDictionary(p => p.Id, p => p.Rank);

        bool CouldTakeOne(int rank)
        {
            return stuck.Any(j => j.MinWorkerPriority == null || (rankById.TryGetValue(j.MinWorkerPriority.Id, out var minimum) && minimum <= rank));
        }

        var highest = workerPriorities.FirstOrDefault(p => p.Rank == highestAvailableRank);
        return new UnsatisfiableJobsReport
        {
            HighestAvailableWorkerPriority = highest != null ? PriorityRepository.ToRef(highest) : null,
            Jobs = stuck,
            NoRoomJobs = noRoom,
            PausedWorkers = availability.PausedButOtherwiseAvailable.Where(w => CouldTakeOne(w.Rank)).Select(w => w.Name).ToList(),
            InUseWorkers = availability.InUseButOtherwiseAvailable.Where(w => CouldTakeOne(w.Rank)).Select(w => w.Name).ToList(),
            UnreachableWorkers = failedToWake.Where(w => CouldTakeOne(w.Rank)).Select(w => w.Name).ToList()
        };
    }

    /// <summary>
    /// These worker views, each on offer — ready, as dispatch sees it, or asleep and to be woken, as waking does — with <see cref="WorkerView.NoRoom"/> filled in where what else is on its disk keeps it from every queued job it could take (<see cref="DispatchPlan.RoomShortfall"/>). A read-only snapshot, taken without locks.
    /// </summary>
    public async Task<IReadOnlyList<WorkerView>> WithNoRoomAsync(IReadOnlyList<WorkerView> views)
    {
        using var scope = _scopeFactory.CreateScope();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

        var ready = (await workers.GetReadyWorkersByPriorityAsync()).Select(w => (w.WorkerId, w.Rank));
        var (wakeable, _) = await _waker.GetSleepersAsync();
        var shown = views.ToDictionary(v => v.Id);
        var onOffer = ready.Concat(wakeable.Select(w => (WorkerId: w.Id, w.Rank))).Where(w => shown.ContainsKey(w.WorkerId)).ToList();
        if (onOffer.Count == 0)
        {
            return views;
        }

        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
        var queued = (await jobs.GetQueuedForDispatchAsync(workers.GetFleetDisks())).Select(q => q.Dispatch).ToList();
        var noRoom = onOffer
            .Select(w => (w.WorkerId, NoRoom: DispatchPlan.RoomShortfall(w.Rank, workers.DiskOf(w.WorkerId), shown[w.WorkerId].UsageMeasured ? shown[w.WorkerId].MirrorsSizeBytes : null, queued)))
            .Where(w => w.NoRoom != null)
            .ToDictionary(w => w.WorkerId, w => w.NoRoom);
        return views.Select(v => noRoom.TryGetValue(v.Id, out var n) ? v with { NoRoom = n } : v).ToList();
    }

    /// <summary>
    /// Queued jobs, other than those no worker on offer qualifies for (<paramref name="priorityStuck"/>), that no worker on offer they qualify for has room for, by the same rule dispatch goes by (<see cref="DiskEstimate.Fit"/>). A busy worker is judged by its latest reading, taken while its job runs, so a job waiting only for it to finish may be listed for a while; a worker that hasn't reported its disk has room for anything.
    /// </summary>
    private static async Task<IReadOnlyList<NoRoomJob>> GetNoRoomJobsAsync(JobRepository jobs, WorkerRepository workers, IReadOnlyList<(Guid Id, int Rank)> onOffer, IReadOnlySet<Guid> priorityStuck)
    {
        if (onOffer.Count == 0)
        {
            return [];
        }

        var queued = (await jobs.GetQueuedForDispatchAsync(workers.GetFleetDisks())).Where(j => !priorityStuck.Contains(j.Row.Id)).OrderBy(j => j.Row.CreatedAt).ToList();
        var disks = onOffer.Select(w => (w.Rank, Disk: workers.DiskOf(w.Id))).ToList();

        var noRoom = new List<NoRoomJob>();
        foreach (var (job, wanted) in queued.Select(q => (q.Row, q.Dispatch)))
        {
            var qualified = disks.Where(w => DispatchPlan.Qualifies(w.Rank, wanted.MinWorkerRank)).Select(w => w.Disk).ToList();
            if (qualified.Count == 0 || qualified.Any(d => DiskEstimate.Fit(wanted.Workspace, wanted.Need, d) != null))
            {
                continue;
            }

            // Both numbers from the worker nearest to fitting, on its nearer way in, so the room never seems to cover the need. Every one of them has reported its disk: one that hasn't would have fitted.
            var nearest = qualified.Select(d => DiskEstimate.Gap(wanted.Workspace, wanted.Need, d)!).MinBy(g => g.ShortBytes)!;
            if (await jobs.GetAsync(job.Id) is { } info)
            {
                noRoom.Add(new NoRoomJob { Job = info, NeededBytes = nearest.NeededBytes, MostRoomBytes = Math.Max(0, nearest.RoomBytes) });
            }
        }
        return noRoom;
    }

    /// <summary>
    /// How long the fleet will take to get through everything running and queued now (<see cref="QueueForecast"/>). A read-only snapshot, taken without locks, like <see cref="GetUnsatisfiableJobsAsync"/>.
    /// </summary>
    public async Task<QueueEstimate> GetQueueEstimateAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

        var availability = await workers.GetAvailabilityAsync();
        var available = availability.Available;
        var unfinished = await jobs.GetUnfinishedJobsAsync();
        var now = DateTime.UtcNow;

        var running = unfinished
            .Where(j => j.Status == JobStatus.Running)
            .Select(j => new QueueForecast.RunningJob(j.WorkerId, j.RepoUrl, j.ScriptPath, now - (j.StartedAt ?? now)))
            .ToList();
        var queued = unfinished
            .Where(j => j.Status == JobStatus.Queued)
            .Select(j => new QueueForecast.QueuedJob(j.MinWorkerRank, j.RepoUrl, j.ScriptPath))
            .ToList();
        var estimates = await jobs.GetScriptEstimatesAsync(unfinished.Select(j => (j.RepoUrl, j.ScriptPath)));

        return QueueForecast.Estimate(available, availability.Connected, running, queued, estimates);
    }

    /// <summary>
    /// Applies a worker's outcome for one of its assignments, idempotently: a completion only for that assignment, a hand-back only while the job is still Running under it. Returns true when the outcome newly failed the job, so the worker now owes a host re-check (already recorded in the database); not for a job its worker stopped for disk, which says nothing about the host's health. Caller holds the worker's session lock.
    /// </summary>
    public async Task<bool> ApplyOutcomeAsync(WorkerSession session, JobOutcome outcome)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();

        if (outcome.Kind == JobOutcomeKind.Rejected)
        {
            var punted = await jobs.GetPuntedAssignmentAsync(outcome.JobId) == outcome.AssignmentId;
            if (await jobs.RequeueAsync(outcome.JobId, session.WorkerId, outcome.AssignmentId))
            {
                _logger.LogInformation("Worker {WorkerId} was busy when job {JobId} arrived; queued it again", session.WorkerId, outcome.JobId);
                if (punted)
                {
                    await _logStream.AppendLineAsync(outcome.JobId, "The worker handed the job back before starting it, so there was no run to stop; it is queued again.");
                }
                _uiEvents.NotifyJobsChanged();
            }
            return false;
        }

        if (outcome.Status is not { } status || status is not (JobStatus.Success or JobStatus.Failed or JobStatus.Cancelled))
        {
            _logger.LogWarning("Worker {WorkerId} reported job {JobId} completed with status {Status}; ignoring", session.WorkerId, outcome.JobId, outcome.Status);
            return false;
        }

        // The run an operator punted is over. Stopped, the job goes back in the queue; finished anyway, it completes, but the log says the punt came too late first, where a reader stopping at the terminal status still sees it.
        if (await jobs.GetPuntedAssignmentAsync(outcome.JobId) == outcome.AssignmentId)
        {
            if (status == JobStatus.Cancelled)
            {
                await RequeuePuntedAsync(session.WorkerId, outcome.JobId, outcome.AssignmentId, "stopped (the cancel above was the punt)");
                return false;
            }
            await _logStream.AppendLineAsync(outcome.JobId, $"The run finished ({status}) before the punt reached it, so the job is not queued again.");
        }

        // Persist the buffered tail before the status turns terminal, as CompleteJobAsync does.
        await _logStream.FlushJobAsync(outcome.JobId);
        if (!await jobs.CompleteReportedAsync(outcome.JobId, session.WorkerId, outcome.AssignmentId, status, outcome.DiskUse))
        {
            _logger.LogInformation("Outcome {Status} of job {JobId} from worker {WorkerId} changed nothing: already applied, or not this assignment", status, outcome.JobId, session.WorkerId);
            await _logStream.FlushAsync(outcome.JobId);
            return false;
        }

        _logger.LogInformation("Job completed: {JobId} - {Status}", outcome.JobId, status);
        await EmitCompletionAsync(scope, outcome.JobId, status);
        return status == JobStatus.Failed && outcome.DiskUse?.StoppedForDisk != true;
    }

    /// <summary>
    /// Status-write-gated entry for completions the server decides on its own (a job lost with its worker's process, one whose token can't be decrypted): persist the terminal status and, if the write actually changed it, emit the completion signal. If the write is refused (the job is already terminal), the signal is suppressed and only the log buffer is flushed, so chunks streamed after the cancel still persist. EmitCompletionAsync owns the side effects themselves; CancelJobAsync calls it directly because TryCancelAsync is its own atomic terminal transition. Returns whether the status changed.
    /// </summary>
    public async Task<bool> CompleteJobAsync(Guid jobId, JobStatus status)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();

        // Persist the buffered tail before the status turns terminal: a log reader stops at a terminal status plus an empty read, and the completion flush below drops the buffer before its insert lands, where the reader's own flush can't see it.
        await _logStream.FlushJobAsync(jobId);

        if (!await jobs.UpdateStatusAsync(jobId, status))
        {
            await _logStream.FlushAsync(jobId);
            return false;
        }

        await EmitCompletionAsync(scope, jobId, status);
        return true;
    }

    /// <summary>
    /// Fails a job the worker's process died with: only while it is still Running for that worker, so a cancel that got there first stands (a server's guess must not override a person's decision, as a real outcome may). Caller holds the worker's session lock.
    /// </summary>
    public async Task<bool> FailLostAsync(Guid jobId, Guid workerId)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();

        await _logStream.FlushJobAsync(jobId);
        if (!await jobs.FailLostAsync(jobId, workerId))
        {
            await _logStream.FlushAsync(jobId);
            return false;
        }

        await EmitCompletionAsync(scope, jobId, JobStatus.Failed);
        return true;
    }

    private async Task EmitCompletionAsync(IServiceScope scope, Guid jobId, JobStatus status)
    {
        await _logStream.NotifyJobCompletedAsync(jobId, status);

        var notifications = scope.ServiceProvider.GetRequiredService<NotificationRepository>();
        await _notificationLock.WaitAsync();
        try
        {
            await notifications.CreateForCompletionAsync(jobId, status);
        }
        finally
        {
            _notificationLock.Release();
        }

        _uiEvents.NotifyJobsChanged();
    }

    /// <summary>
    /// Cancels a job, telling the worker it is assigned to, if any. For an assigned job the whole step runs under that worker's lock, like every other change to a worker's jobs: the CancelJob is queued after any AssignJob for the job, and a Resume can't decide about the job halfway through. Dispatches afterwards, since the worker may now be free.
    /// </summary>
    public async Task<bool> CancelJobAsync(Guid jobId)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();

        bool cancelled;
        while (true)
        {
            var owner = await jobs.GetAssignedWorkerAsync(jobId);
            if (owner == null)
            {
                cancelled = await CancelUnderLockAsync(scope, jobs, jobId, owner: null);
                // Assigned between the read and the cancel: its worker still has to hear about it.
                if (cancelled && await jobs.GetAssignedWorkerAsync(jobId) is { } assignedMeanwhile)
                {
                    using (await _sessions.LockAsync(assignedMeanwhile))
                    {
                        SendCancel(assignedMeanwhile, jobId);
                    }
                }
                break;
            }

            using (await _sessions.LockAsync(owner.Value))
            {
                // A requeue and reassignment can move the job between the read and the lock: retry under the right worker.
                if (await jobs.GetAssignedWorkerAsync(jobId) != owner)
                {
                    continue;
                }

                cancelled = await CancelUnderLockAsync(scope, jobs, jobId, owner);
            }
            break;
        }

        if (cancelled)
        {
            await DispatchAsync();
        }
        return cancelled;
    }

    private async Task<bool> CancelUnderLockAsync(IServiceScope scope, JobRepository jobs, Guid jobId, Guid? owner)
    {
        // The same ordering as CompleteJobAsync, for the same reason.
        await _logStream.FlushJobAsync(jobId);

        if (!await jobs.TryCancelAsync(jobId))
        {
            return false;
        }

        // TryCancelAsync just made the job terminal, so the completion signal fires here; the worker's own later outcome takes effect only if it is a real Success or Failed, which overrides the cancel.
        await EmitCompletionAsync(scope, jobId, JobStatus.Cancelled);
        if (owner != null)
        {
            SendCancel(owner.Value, jobId);
        }
        return true;
    }

    /// <summary>A worker that isn't running the job ignores it; one that reconnects before it arrives is told by its Resume instead. Caller holds the worker's lock.</summary>
    private void SendCancel(Guid workerId, Guid jobId)
    {
        if (_sessions.ByWorker(workerId) is { } session)
        {
            _logger.LogInformation("Sending CancelJob to worker {WorkerId} for job {JobId}", workerId, jobId);
            _messenger.Send(session.ConnectionId, $"cancel of job {jobId}", client => client.CancelJob(jobId.ToString()));
        }
    }

    /// <summary>
    /// Punts a job: tells its worker to stop the run, and once the worker reports it stopped, puts the job back in the queue for any worker, its log kept and carried on (<see cref="RequeuePuntedAsync"/>). The job stays Running for its worker until then, so the stopped run and the next never overlap. Refused for a run other than the one that started at <paramref name="startedAt"/>, which the caller saw, and, unless <paramref name="force"/>, when the job would only come straight back to the same worker. Taken under the worker's lock, like a cancel. <paramref name="source"/> says in the log where the request came from.
    /// </summary>
    public async Task<PuntAnswer?> PuntJobAsync(Guid jobId, DateTime startedAt, bool force, string source)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

        if (await jobs.GetLogRunAsync(jobId) is not { } run)
        {
            return null;
        }
        if (run.Status != JobStatus.Running || await jobs.GetAssignedWorkerAsync(jobId) is not { } owner)
        {
            return new PuntAnswer(PuntStatus.NotRunning, null, WorkerConnected: false);
        }

        var forecast = await ForecastPuntAsync(jobId);
        if (forecast.Outcome == PuntOutcome.ComesBack && !force)
        {
            return new PuntAnswer(PuntStatus.WouldComeBack, forecast, _sessions.ByWorker(owner) != null);
        }

        var workerName = await workers.GetNameAsync(owner) ?? owner.ToString();
        using (await _sessions.LockAsync(owner))
        {
            var connected = _sessions.ByWorker(owner) != null;
            var result = await jobs.TryRequestPuntAsync(jobId, owner, startedAt);
            if (result == PuntRequestResult.Stale)
            {
                return new PuntAnswer(PuntStatus.Stale, forecast, connected);
            }
            if (result == PuntRequestResult.AlreadyRequested)
            {
                return new PuntAnswer(PuntStatus.AlreadyRequested, forecast, connected);
            }

            // Sent once: the messenger only loses it with the connection, and then the worker's Resume is told to stop the run instead.
            SendCancel(owner, jobId);
            var when = connected ? "" : $" once it reconnects (it isn't connected now)";
            await _logStream.AppendLineAsync(jobId, $"Punted from {source} at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC: stopping this run on {workerName}{when}; the job goes back in the queue when it has stopped.");
            _logger.LogInformation("Punting job {JobId} from worker {WorkerId}, as asked from {Source}", jobId, owner, source);
            _uiEvents.NotifyJobsChanged();
            return new PuntAnswer(PuntStatus.Requested, forecast, connected);
        }
    }

    /// <summary>Where the job would go if it were punted now (<see cref="PuntPlan"/>): a snapshot, taken without locks, like <see cref="GetQueueEstimateAsync"/>. For a job that isn't running, where it would go from the queue.</summary>
    public async Task<PuntForecastInfo> ForecastPuntAsync(Guid jobId)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

        var owner = await jobs.GetAssignedWorkerAsync(jobId);
        var queue = await jobs.GetQueuedForDispatchAsync(workers.GetFleetDisks(), asIfQueued: jobId);
        var punted = queue.FirstOrDefault(q => q.Row.Id == jobId);
        if (punted == null)
        {
            return new PuntForecastInfo { Outcome = PuntOutcome.Waits };
        }

        var others = (await workers.GetReadyWorkersByPriorityAsync()).Where(w => w.WorkerId != owner).ToList();
        var punterOnceIdle = owner is { } o ? await workers.GetOnceIdleAsync(o) : null;
        var prediction = PuntPlan.Predict(queue, q => q.Dispatch, punted, others, punterOnceIdle, _hold.Held);
        return new PuntForecastInfo
        {
            Outcome = prediction.Outcome,
            WorkerId = prediction.WorkerId,
            WorkerName = prediction.WorkerId is { } w ? await workers.GetNameAsync(w) : null,
            Draining = prediction.Draining
        };
    }

    /// <summary>
    /// Puts a punted job back in the queue now that its run is over (the worker reported it stopped, or came back without it): the log says so and carries on after that line, and what belonged to the stopped run goes (<see cref="JobRepository.RequeuePuntedAsync"/>). <paramref name="assignmentId"/> is the punted run's, or null for whichever is punted. Doesn't dispatch: the caller holds the worker's session lock, and dispatches once it has let go. Returns whether the job was requeued.
    /// </summary>
    public async Task<bool> RequeuePuntedAsync(Guid workerId, Guid jobId, Guid? assignmentId, string how)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

        var workerName = await workers.GetNameAsync(workerId) ?? workerId.ToString();
        var commit = (await jobs.GetAsync(jobId))?.Commit;

        // Under the job's log lock from the line's number to the next run's start, so no chunk lands in between, and the next run's output comes after the line.
        var dropped = await _logStream.WithServerLogAsync(jobId, async log =>
        {
            var sequence = await log.NextSequenceAsync();
            var artifacts = await jobs.RequeuePuntedAsync(jobId, workerId, assignmentId, sequence);
            if (artifacts == null)
            {
                return null;
            }

            var built = commit != null ? $" (commit {commit[..Math.Min(12, commit.Length)]})" : "";
            log.StartNextRun();
            await log.WriteLineAsync(sequence, $"The punted run {how} on {workerName}{built}; dropped its metrics, step timings and {artifacts.Count} artifact(s). Queued again; the next run's output follows.");
            return artifacts;
        });
        if (dropped == null)
        {
            return false;
        }

        foreach (var (path, error) in scope.ServiceProvider.GetRequiredService<ArtifactRepository>().DeleteFiles(dropped))
        {
            _logger.LogWarning(error, "Couldn't delete artifact file {Path} of punted job {JobId}; left behind", path, jobId);
        }
        _logger.LogInformation("Punted job {JobId} {How} on worker {WorkerId}; queued it again", jobId, how, workerId);
        _uiEvents.NotifyJobsChanged();
        return true;
    }

    /// <summary>
    /// An operator's request for a fresh diagnostic. The worker takes no work until one has started, then until it reports the verdict. Returns false if the worker has no resumed session to ask.
    /// </summary>
    public async Task<bool> RequestDiagnosticAsync(Guid workerId)
    {
        using var scope = _scopeFactory.CreateScope();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

        using (await _sessions.LockAsync(workerId))
        {
            if (_sessions.ByWorker(workerId) is not { Resumed: true } session)
            {
                return false;
            }

            await workers.SetDiagnosticOwedAsync(workerId, true);
            _sessions.SetDiagnosticOwed(workerId, true);
            _messenger.Send(session.ConnectionId, "operator diagnostic", client => client.RunDiagnostic());
        }

        _uiEvents.NotifyWorkersChanged();
        return true;
    }
}
