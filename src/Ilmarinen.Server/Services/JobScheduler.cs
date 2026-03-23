using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUlid;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Ilmarinen.Server.Services;

public class JobScheduler
{
    private readonly ConcurrentQueue<Ulid> _pendingJobs = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<WorkerHub, IWorkerClient> _hubContext;
    private readonly ILogger<JobScheduler> _logger;
    private bool _initialized;
    private readonly object _initLock = new();

    public JobScheduler(
        IServiceScopeFactory scopeFactory,
        IHubContext<WorkerHub, IWorkerClient> hubContext,
        ILogger<JobScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
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

    public async Task<Ulid> EnqueueJobAsync(JobSubmission submission, Ulid? pipelineId = null)
    {
        await EnsureInitializedAsync();

        using var scope = _scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

        var info = await jobs.CreateAsync(submission, pipelineId);
        await jobs.UpdateStatusAsync(info.Id, JobStatus.Queued);
        _pendingJobs.Enqueue(info.Id);

        // Try to dispatch immediately if there's an idle worker
        var readyConnectionId = await workers.FindReadyWorkerConnectionIdAsync();
        if (readyConnectionId != null)
        {
            await TryAssignJobAsync(readyConnectionId);
        }

        return info.Id;
    }

    public async Task<bool> TryAssignJobAsync(string connectionId)
    {
        await EnsureInitializedAsync();

        using var scope = _scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

        var worker = await workers.GetByConnectionIdAsync(connectionId);
        if (worker == null || !worker.IsReady)
            return false;

        if (!_pendingJobs.TryDequeue(out var jobId))
            return false;

        var submission = await jobs.GetSubmissionAsync(jobId);
        if (submission == null)
            return false;

        var assignment = new JobAssignment
        {
            Id = jobId,
            RepoUrl = submission.RepoUrl,
            Ref = submission.Ref,
            ScriptPath = submission.ScriptPath,
            GitToken = submission.GitToken
        };

        await jobs.UpdateStatusAsync(jobId, JobStatus.Running, worker.Id);
        await workers.SetCurrentJobAsync(connectionId, jobId);

        await _hubContext.Clients.Client(connectionId).AssignJob(assignment);
        return true;
    }

    public async Task CompleteJobAsync(Ulid jobId, JobResult result)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
        await jobs.UpdateStatusAsync(jobId, result.Status);

        var notifications = scope.ServiceProvider.GetRequiredService<NotificationRepository>();
        await notifications.CreateForActiveSubscribersAsync(jobId, "JobCompleted");
    }

    public async Task<bool> CancelJobAsync(Ulid jobId)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

        // Find the worker running this job before cancelling (status change clears the assignment)
        var connectionId = workers.FindConnectionIdByJobId(jobId);

        var cancelled = await jobs.TryCancelAsync(jobId);
        if (!cancelled)
            return false;

        // If a worker is actively running this job, tell it to stop
        if (connectionId != null)
        {
            _logger.LogInformation("Sending CancelJob to worker for job {JobId}", jobId);
            await _hubContext.Clients.Client(connectionId).CancelJob(jobId.ToString());
        }

        return true;
    }
}
