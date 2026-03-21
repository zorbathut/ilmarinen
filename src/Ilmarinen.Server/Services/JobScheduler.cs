using System.Collections.Concurrent;
using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using NUlid;

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

    public async Task<Ulid> EnqueueJobAsync(JobSubmission submission)
    {
        await EnsureInitializedAsync();

        using var scope = _scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

        var info = await jobs.CreateAsync(submission);
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
    }

    public async Task<bool> CancelJobAsync(Ulid jobId)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
        return await jobs.TryCancelAsync(jobId);
    }
}
