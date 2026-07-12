using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Ilmarinen.Server.Services;

public class JobScheduler
{
    private readonly ConcurrentQueue<Guid> _pendingJobs = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<WorkerHub, IWorkerClient> _hubContext;
    private readonly UIEventService _uiEvents;
    private readonly LogStreamService _logStream;
    private readonly ILogger<JobScheduler> _logger;
    private bool _initialized;
    private readonly object _initLock = new();

    // Serializes the check-and-assign sequence within this process. Assignment is triggered concurrently from multiple places (JobCompleted, Ready, EnqueueJob), and the window between "is this worker free?" and "mark it busy" must be atomic.
    private readonly SemaphoreSlim _assignLock = new(1, 1);

    public JobScheduler(
        IServiceScopeFactory scopeFactory,
        IHubContext<WorkerHub, IWorkerClient> hubContext,
        UIEventService uiEvents,
        LogStreamService logStream,
        ILogger<JobScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _uiEvents = uiEvents;
        _logStream = logStream;
        _logger = logger;
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;

        lock (_initLock)
        {
            if (_initialized) return;
            _initialized = true;
        }

        // Load queued jobs from database
        using var scope = _scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
        var queuedIds = await jobs.GetQueuedJobIdsAsync();

        foreach (var id in queuedIds)
        {
            _pendingJobs.Enqueue(id);
        }

        _logger.LogInformation("Loaded {Count} queued jobs from database", queuedIds.Count);
    }

    public async Task<Guid> EnqueueJobAsync(JobSubmission submission)
    {
        await EnsureInitializedAsync();

        using var scope = _scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();

        var info = await jobs.CreateAsync(submission);
        await jobs.UpdateStatusAsync(info.Id, JobStatus.Queued);
        _pendingJobs.Enqueue(info.Id);
        _uiEvents.NotifyJobsChanged();

        await DispatchAsync();

        return info.Id;
    }

    /// <summary>
    /// Hands queued jobs to ready workers, highest-priority worker first. Called whenever the pool or the queue changes — a job is submitted, a worker signals Ready, a worker finishes a job — and keeps going until the queue drains or no ready worker will take another job, so a queued job can never sit behind an idle worker.
    /// </summary>
    public async Task DispatchAsync()
    {
        await EnsureInitializedAsync();

        await _assignLock.WaitAsync();
        try
        {
            // Nothing to place. Bail before touching the database — every worker signalling Ready on an idle fleet comes through here.
            if (_pendingJobs.IsEmpty)
            {
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
            var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

            // One pass is enough: each candidate takes at most one job, and after that it is busy. A worker that frees up meanwhile raises its own DispatchAsync, which is waiting on this lock.
            foreach (var connectionId in await workers.GetReadyConnectionIdsByPriorityAsync())
            {
                if (_pendingJobs.IsEmpty)
                {
                    return;
                }

                var worker = workers.GetByConnectionId(connectionId);
                if (worker == null || !worker.IsReady)
                {
                    continue;
                }

                // The in-memory ready flag can be set redundantly — the worker sends an explicit Ready after every JobCompleted, and the JobCompleted handler marks it ready too. The DB is the source of truth for "busy": a worker already running a job must never be handed another, and a stale Ready that put the flag up gets corrected here.
                if (await jobs.GetRunningJobForWorkerAsync(worker.Id) != null)
                {
                    workers.SetReady(connectionId, false);
                    continue;
                }

                // One unreachable worker must not strand the jobs the rest of the fleet could still take. Its job is already marked Running against it, and is reconciled like any other undelivered job when the worker reconnects.
                try
                {
                    await AssignNextJobAsync(jobs, workers, connectionId, worker.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to dispatch to worker {WorkerId}; trying the next candidate", worker.Id);
                    workers.SetReady(connectionId, false);
                }
            }
        }
        finally
        {
            _assignLock.Release();
        }
    }

    /// <summary>
    /// Pops queued jobs until one is successfully handed to this worker. Caller holds _assignLock and has already confirmed the worker is idle.
    /// </summary>
    private async Task AssignNextJobAsync(JobRepository jobs, WorkerRepository workers, string connectionId, Guid workerId)
    {
        while (_pendingJobs.TryDequeue(out var jobId))
        {
            var submission = await jobs.GetSubmissionAsync(jobId);
            if (submission == null)
            {
                _logger.LogError("Dequeued job {JobId} has no submission record; dropping it", jobId);
                continue;
            }

            // Gate the assignment on the status write: a refused write means the job went terminal (cancelled) while queued, so skip it. A cancel can still land between this write and the AssignJob send — the worker drops a CancelJob for a job it hasn't yet registered as running, and the override rule reconciles the eventual outcome.
            if (!await jobs.UpdateStatusAsync(jobId, JobStatus.Running, workerId))
            {
                _logger.LogInformation("Skipping dequeued job {JobId}: cancelled while queued", jobId);
                continue;
            }

            var assignment = new JobAssignment
            {
                Id = jobId,
                RepoUrl = submission.RepoUrl!,
                Ref = submission.Ref!,
                ScriptPath = submission.ScriptPath!,
                GitToken = submission.GitToken
            };

            workers.SetReady(connectionId, false);
            _uiEvents.NotifyJobsChanged();

            await _hubContext.Clients.Client(connectionId).AssignJob(assignment);
            return;
        }
    }

    /// <summary>
    /// Status-write-gated entry for reported completions (worker JobCompleted, orphaned-job failure on reconnect): persist the terminal status and, if the write actually changed it, emit the completion signal. If the write is refused (the job is already terminal — e.g. the worker's Cancelled report after CancelJobAsync already signalled), the signal is suppressed and only the log buffer is flushed, so chunks streamed after the cancel still persist. EmitCompletionAsync owns the side effects themselves; CancelJobAsync calls it directly because TryCancelAsync is its own atomic terminal transition.
    /// </summary>
    public async Task CompleteJobAsync(Guid jobId, JobStatus status)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
        if (!await jobs.UpdateStatusAsync(jobId, status))
        {
            await _logStream.FlushAsync(jobId);
            return;
        }

        await EmitCompletionAsync(scope, jobId, status);
    }

    private async Task EmitCompletionAsync(IServiceScope scope, Guid jobId, JobStatus status)
    {
        await _logStream.NotifyJobCompletedAsync(jobId, status);

        var notifications = scope.ServiceProvider.GetRequiredService<NotificationRepository>();
        await notifications.CreateForActiveSubscribersAsync(jobId, "JobCompleted");

        _uiEvents.NotifyJobsChanged();
    }

    public async Task<bool> CancelJobAsync(Guid jobId)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

        // Find the worker running this job before cancelling (status change clears the assignment)
        var connectionId = workers.FindConnectionIdByJobId(jobId);

        var cancelled = await jobs.TryCancelAsync(jobId);
        if (!cancelled)
            return false;

        // TryCancelAsync just made the job terminal, so the completion signal fires here; any later JobCompleted report from the worker is suppressed by CompleteJobAsync's refused-write guard (unless it overrides with the real outcome).
        await EmitCompletionAsync(scope, jobId, JobStatus.Cancelled);

        // If a worker is actively running this job, tell it to stop
        if (connectionId != null)
        {
            _logger.LogInformation("Sending CancelJob to worker for job {JobId}", jobId);
            await _hubContext.Clients.Client(connectionId).CancelJob(jobId.ToString());
        }

        return true;
    }
}
