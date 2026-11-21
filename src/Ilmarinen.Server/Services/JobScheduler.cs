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

public class JobScheduler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WorkerMessenger _messenger;
    private readonly WorkerSessions _sessions;
    private readonly UIEventService _uiEvents;
    private readonly LogStreamService _logStream;
    private readonly WorkerWaker _waker;
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
        ILogger<JobScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _messenger = messenger;
        _sessions = sessions;
        _uiEvents = uiEvents;
        _logStream = logStream;
        _waker = waker;
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
    /// Hands queued jobs to ready workers: each queued job in dispatch order (highest job priority, then oldest) goes to the highest-priority ready worker that qualifies for it, preferring one that holds its workspace (<see cref="DispatchPlan"/>). Nothing running is ever displaced for a higher-priority job. Called whenever readiness or the queue may have changed — a job is submitted or handed back, a worker resumes or reports a new state, a worker's priority changes or it is unpaused — and keeps going until no ready worker will take another job, so a queued job never sits behind an idle worker that meets its minimum priority.
    ///
    /// The matching is greedy: a job offered first takes its best worker even when a later job could only have run there, and then waits.
    /// </summary>
    public async Task DispatchAsync()
    {
        await _assignLock.WaitAsync();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
            var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

            // One pass is enough: each worker takes at most one job, and after that it is busy. A worker that frees up meanwhile, or a job submitted meanwhile, raises its own DispatchAsync, which is waiting on this lock.
            var free = (await workers.GetReadyWorkersByPriorityAsync()).ToList();
            if (free.Count == 0)
            {
                return;
            }

            var queued = await jobs.GetQueuedJobsAsync();
            var history = await jobs.GetDiskHistoryAsync(queued.Select(j => (j.RepoUrl, j.ScriptPath)));
            var fleet = workers.GetFleetDisks();
            foreach (var job in queued)
            {
                if (free.Count == 0)
                {
                    return;
                }

                var runs = history.GetValueOrDefault((job.RepoUrl, job.ScriptPath)) ?? [];
                var wanted = new DispatchJob(job.MinWorkerRank, DiskEstimate.PredictedWorkspace(runs), DiskEstimate.NeedOnFleet(runs, fleet));
                while (DispatchPlan.Pick(wanted, free) is { Worker: var worker, Fit: var fit })
                {
                    // The list was a snapshot; under the worker's lock nothing about it can change while it is checked and handed a job. One no longer ready is out of this pass, and the job goes to the next best.
                    using (await _sessions.LockAsync(worker.WorkerId))
                    {
                        var session = _sessions.ByWorker(worker.WorkerId);
                        if (session == null || !await workers.IsReadyAsync(session))
                        {
                            free.Remove(worker);
                            continue;
                        }

                        if (await TryAssignAsync(jobs, session, job.Id, wanted, fit))
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
    private async Task<bool> TryAssignAsync(JobRepository jobs, WorkerSession session, Guid jobId, DispatchJob wanted, DiskFit fit)
    {
        var assignmentId = Guid.NewGuid();
        JobAssignment assignment;
        try
        {
            assignment = await jobs.GetAssignmentAsync(jobId, assignmentId) with { PredictedWorkspace = wanted.Workspace, DiskNeededBytes = fit.NeededBytes, StartFresh = fit.StartFresh, DiskNeededToReuseBytes = wanted.Need.ReuseBytes };
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
    /// Pauses or unpauses a worker; unpausing dispatches, since the worker may be ready for a job that is waiting. A paused worker is sent no new jobs, connected or not; what it is running carries on. Returns false if the worker doesn't exist.
    /// </summary>
    public async Task<bool> SetWorkerPausedAsync(Guid workerId, bool paused)
    {
        using var scope = _scopeFactory.CreateScope();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

        // Under the dispatch lock, so once a pause returns no dispatch can still be acting on the worker as unpaused.
        var updated = await UnderDispatchLockAsync(() => workers.SetPausedAsync(workerId, paused));

        if (!updated)
        {
            return false;
        }

        _uiEvents.NotifyWorkersChanged();
        if (!paused)
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
    /// Queued jobs, other than those no worker on offer qualifies for (<paramref name="priorityStuck"/>), that no worker on offer they qualify for has room for, by the same rule dispatch goes by (<see cref="DiskEstimate.Fit"/>). A busy worker is judged by its latest reading, taken while its job runs, so a job waiting only for it to finish may be listed for a while; a worker that hasn't reported its disk has room for anything.
    /// </summary>
    private static async Task<IReadOnlyList<NoRoomJob>> GetNoRoomJobsAsync(JobRepository jobs, WorkerRepository workers, IReadOnlyList<(Guid Id, int Rank)> onOffer, IReadOnlySet<Guid> priorityStuck)
    {
        var queued = (await jobs.GetQueuedJobsAsync()).Where(j => !priorityStuck.Contains(j.Id)).OrderBy(j => j.CreatedAt).ToList();
        if (queued.Count == 0 || onOffer.Count == 0)
        {
            return [];
        }

        var history = await jobs.GetDiskHistoryAsync(queued.Select(j => (j.RepoUrl, j.ScriptPath)));
        var fleet = workers.GetFleetDisks();
        var disks = onOffer.Select(w => (w.Rank, Disk: workers.DiskOf(w.Id))).ToList();

        var noRoom = new List<NoRoomJob>();
        foreach (var job in queued)
        {
            var runs = history.GetValueOrDefault((job.RepoUrl, job.ScriptPath)) ?? [];
            var workspace = DiskEstimate.PredictedWorkspace(runs);
            var need = DiskEstimate.NeedOnFleet(runs, fleet);
            var qualified = disks.Where(w => DispatchPlan.Qualifies(w.Rank, job.MinWorkerRank)).Select(w => w.Disk).ToList();
            if (qualified.Count == 0 || qualified.Any(d => DiskEstimate.Fit(workspace, need, d) != null))
            {
                continue;
            }

            // What the cheapest way in would take: reusing the workspace where a worker holds it.
            var needed = workspace != null && qualified.Any(d => d.Workspaces.ContainsKey(workspace)) ? need.ReuseBytes : need.FreshBytes;
            if (await jobs.GetAsync(job.Id) is { } info)
            {
                noRoom.Add(new NoRoomJob { Job = info, NeededBytes = needed, MostRoomBytes = qualified.Max(d => d.BestRoomBytes) });
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
            if (await jobs.RequeueAsync(outcome.JobId, session.WorkerId, outcome.AssignmentId))
            {
                _logger.LogInformation("Worker {WorkerId} was busy when job {JobId} arrived; queued it again", session.WorkerId, outcome.JobId);
                _uiEvents.NotifyJobsChanged();
            }
            return false;
        }

        if (outcome.Status is not { } status || status is not (JobStatus.Success or JobStatus.Failed or JobStatus.Cancelled))
        {
            _logger.LogWarning("Worker {WorkerId} reported job {JobId} completed with status {Status}; ignoring", session.WorkerId, outcome.JobId, outcome.Status);
            return false;
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
