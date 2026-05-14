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
    private readonly ILogger<JobScheduler> _logger;
    private bool _initialized;
    private readonly object _initLock = new();

    // Serializes the check-and-assign sequence within this process. Assignment is
    // triggered concurrently from multiple places (JobCompleted, Ready, EnqueueJob),
    // and the window between "is this worker free?" and "mark it busy" must be atomic.
    private readonly SemaphoreSlim _assignLock = new(1, 1);

    public JobScheduler(
        IServiceScopeFactory scopeFactory,
        IHubContext<WorkerHub, IWorkerClient> hubContext,
        UIEventService uiEvents,
        ILogger<JobScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _uiEvents = uiEvents;
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
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

        var info = await jobs.CreateAsync(submission);
        await jobs.UpdateStatusAsync(info.Id, JobStatus.Queued);
        _pendingJobs.Enqueue(info.Id);
        _uiEvents.NotifyJobsChanged();

        // Try to dispatch immediately if there's an idle worker
        var readyConnectionId = workers.FindReadyWorkerConnectionId();
        if (readyConnectionId != null)
        {
            await TryAssignJobAsync(readyConnectionId);
        }

        return info.Id;
    }

    public async Task<bool> TryAssignJobAsync(string connectionId)
    {
        await EnsureInitializedAsync();

        await _assignLock.WaitAsync();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
            var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

            var worker = workers.GetByConnectionId(connectionId);
            if (worker == null || !worker.IsReady)
                return false;

            // The in-memory ready flag can be set redundantly — the worker sends an
            // explicit Ready after every JobCompleted, and the JobCompleted handler
            // marks it ready too. The DB is the source of truth for "busy": a worker
            // already running a job must never be handed another, and a stale Ready
            // that put the flag up gets corrected here.
            if (await jobs.GetRunningJobForWorkerAsync(worker.Id) != null)
            {
                workers.SetReady(connectionId, false);
                return false;
            }

            if (!_pendingJobs.TryDequeue(out var jobId))
                return false;

            var submission = await jobs.GetSubmissionAsync(jobId);
            if (submission == null)
            {
                _logger.LogError("Dequeued job {JobId} has no submission record; dropping it", jobId);
                return false;
            }

            var assignment = new JobAssignment
            {
                Id = jobId,
                RepoUrl = submission.RepoUrl!,
                Ref = submission.Ref!,
                ScriptPath = submission.ScriptPath!,
                GitToken = submission.GitToken
            };

            await jobs.UpdateStatusAsync(jobId, JobStatus.Running, worker.Id);
            workers.SetReady(connectionId, false);
            _uiEvents.NotifyJobsChanged();

            await _hubContext.Clients.Client(connectionId).AssignJob(assignment);
            return true;
        }
        finally
        {
            _assignLock.Release();
        }
    }

    public async Task CompleteJobAsync(Guid jobId, JobResult result)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
        await jobs.UpdateStatusAsync(jobId, result.Status);
        _uiEvents.NotifyJobsChanged();

        var notifications = scope.ServiceProvider.GetRequiredService<NotificationRepository>();
        await notifications.CreateForActiveSubscribersAsync(jobId, "JobCompleted");
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

        _uiEvents.NotifyJobsChanged();

        // If a worker is actively running this job, tell it to stop
        if (connectionId != null)
        {
            _logger.LogInformation("Sending CancelJob to worker for job {JobId}", jobId);
            await _hubContext.Clients.Client(connectionId).CancelJob(jobId.ToString());
        }

        return true;
    }
}
