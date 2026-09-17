using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Ilmarinen.Server.Services;

public class JobScheduler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<WorkerHub, IWorkerClient> _hubContext;
    private readonly UIEventService _uiEvents;
    private readonly LogStreamService _logStream;
    private readonly ILogger<JobScheduler> _logger;

    // Serializes the check-and-assign sequence within this process. Assignment is triggered concurrently from multiple places (JobCompleted, Ready, EnqueueJob), and the windows between "is this worker free?" / "which job is next?" and marking them busy / Running must be atomic.
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
    /// Hands queued jobs to ready workers, highest-priority worker first. Called whenever the pool or the queue changes — a job is submitted, a worker signals Ready, a worker finishes a job, a worker's priority changes — and keeps going until no ready worker will take another job, so a queued job never sits behind an idle worker that meets its minimum priority.
    /// </summary>
    public async Task DispatchAsync()
    {
        await _assignLock.WaitAsync();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
            var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

            // One pass is enough: each candidate takes at most one job, and after that it is busy. A worker that frees up meanwhile raises its own DispatchAsync, which is waiting on this lock.
            foreach (var (connectionId, priority) in await workers.GetReadyWorkersByPriorityAsync())
            {
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

                // Candidates come highest priority first, and a worker qualifies for everything a lower-priority one does, so once nothing queued fits this worker nothing fits the rest.
                if (!await AssignNextJobAsync(jobs, workers, connectionId, worker.Id, priority))
                {
                    return;
                }
            }
        }
        finally
        {
            _assignLock.Release();
        }
    }

    /// <summary>
    /// Takes queued jobs this worker's priority qualifies it for, oldest first, until one is handed to it. Returns false if none fit. Caller holds _assignLock and has already confirmed the worker is idle.
    /// </summary>
    private async Task<bool> AssignNextJobAsync(JobRepository jobs, WorkerRepository workers, string connectionId, Guid workerId, WorkerPriority workerPriority)
    {
        while (await jobs.GetNextQueuedJobIdAsync(workerPriority) is { } jobId)
        {
            JobAssignment assignment;
            try
            {
                assignment = await jobs.GetAssignmentAsync(jobId);
            }
            catch (CryptographicException ex)
            {
                // The stored git token can't be decrypted, typically because the server key changed while the job was queued. No worker can ever run this job, and it is no fault of this one.
                _logger.LogError(ex, "Job {JobId} has a git token that can no longer be decrypted; failing it", jobId);
                await CompleteJobAsync(jobId, JobStatus.Failed);
                continue;
            }

            // Gate the assignment on the status write: a refused write means the job went terminal (cancelled) while queued, so skip it. A cancel can still land between this write and the AssignJob send — the worker drops a CancelJob for a job it hasn't yet registered as running, and the override rule reconciles the eventual outcome.
            if (!await jobs.UpdateStatusAsync(jobId, JobStatus.Running, workerId))
            {
                _logger.LogInformation("Skipping queued job {JobId}: cancelled before it could be assigned", jobId);
                continue;
            }

            workers.SetReady(connectionId, false);
            _uiEvents.NotifyJobsChanged();

            // Only a failed send is the worker's fault. One unreachable worker must not strand the jobs the rest of the fleet could still take: its job is already marked Running against it, and is reconciled like any other undelivered job when the worker reconnects.
            try
            {
                await _hubContext.Clients.Client(connectionId).AssignJob(assignment);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send job {JobId} to worker {WorkerId}; trying the next candidate", jobId, workerId);
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Changes a worker's priority, then dispatches: a promoted worker may now meet the minimum of a job that is waiting. Returns false if the worker doesn't exist.
    /// </summary>
    public async Task<bool> SetWorkerPriorityAsync(Guid workerId, WorkerPriority priority)
    {
        using var scope = _scopeFactory.CreateScope();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

        // Under the dispatch lock, so a demotion can't land between a dispatch reading this worker's priority and handing it a job only the old priority qualified it for.
        bool updated;
        await _assignLock.WaitAsync();
        try
        {
            updated = await workers.SetPriorityAsync(workerId, priority);
        }
        finally
        {
            _assignLock.Release();
        }

        if (!updated)
        {
            return false;
        }

        _uiEvents.NotifyWorkersChanged();
        await DispatchAsync();
        return true;
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
