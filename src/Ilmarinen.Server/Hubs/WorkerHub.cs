using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Server.Hubs;

public class WorkerHub : Hub<IWorkerClient>
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ServerKeyService _serverKey;
    private readonly UIEventService _uiEvents;
    private readonly ILogger<WorkerHub> _logger;

    private static readonly ConcurrentDictionary<string, PendingAuth> _pendingAuths = new();

    public WorkerHub(IServiceScopeFactory scopeFactory, ServerKeyService serverKey, UIEventService uiEvents, ILogger<WorkerHub> logger)
    {
        _scopeFactory = scopeFactory;
        _serverKey = serverKey;
        _uiEvents = uiEvents;
        _logger = logger;
    }

    public async Task<AuthChallenge> Connect(WorkerConnect request)
    {
        // Validate protocol compatibility early
        if (request.ProtocolHash != ProtocolVersion.Hash)
        {
            _logger.LogError(
                "Worker {WorkerId} rejected: protocol mismatch (worker: {WorkerHash}, server: {ServerHash})",
                request.WorkerId, request.ProtocolHash, ProtocolVersion.Hash);

            throw new HubException(
                $"Protocol mismatch. Worker is '{request.ProtocolHash}', server is '{ProtocolVersion.Hash}'. " +
                "Rebuild both with the same protocol definitions.");
        }

        using var scope = _scopeFactory.CreateScope();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

        var publicKey = await workers.GetWorkerPublicKeyAsync(request.WorkerId);
        if (publicKey == null)
        {
            _logger.LogWarning("Connection failed: unknown worker {WorkerId}", request.WorkerId);
            throw new HubException("Unknown worker. Register this worker via the API first.");
        }

        var serverNonce = RandomNumberGenerator.GetBytes(32);
        var challengeData = ServerKeyService.BuildChallengeData(
            request.WorkerId.ToString(), request.Nonce, serverNonce);
        var serverSignature = _serverKey.Sign(challengeData);

        _pendingAuths[Context.ConnectionId] = new PendingAuth
        {
            WorkerId = request.WorkerId,
            WorkerNonce = request.Nonce,
            ServerNonce = serverNonce,
            CreatedAt = DateTime.UtcNow
        };

        _logger.LogDebug("Issued auth challenge for worker {WorkerId}", request.WorkerId);

        return new AuthChallenge
        {
            Nonce = serverNonce,
            ServerSignature = serverSignature,
            ProtocolHash = ProtocolVersion.Hash
        };
    }

    public async Task Authenticate(WorkerAuthenticate info)
    {
        if (!_pendingAuths.TryRemove(Context.ConnectionId, out var pending))
        {
            throw new HubException("No pending authentication challenge. Call Connect first.");
        }

        if (pending.CreatedAt.AddSeconds(60) < DateTime.UtcNow)
        {
            throw new HubException("Authentication challenge expired. Call Connect again.");
        }

        // Verify worker signature
        using var scope = _scopeFactory.CreateScope();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

        var publicKeyBytes = await workers.GetWorkerPublicKeyAsync(pending.WorkerId);
        if (publicKeyBytes == null)
        {
            throw new HubException("Unknown worker.");
        }

        using var workerEcdsa = ECDsa.Create();
        workerEcdsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);

        var challengeData = ServerKeyService.BuildChallengeData(
            pending.WorkerId.ToString(), pending.WorkerNonce, pending.ServerNonce);
        if (!workerEcdsa.VerifyData(challengeData, info.Signature, HashAlgorithmName.SHA256))
        {
            _logger.LogWarning("Worker {WorkerId} rejected: invalid signature", pending.WorkerId);
            throw new HubException("Invalid signature. Check that the worker key is correct.");
        }

        await workers.ConnectAsync(Context.ConnectionId, pending.WorkerId);
        workers.SetWorkspaces(Context.ConnectionId, info.Workspaces);
        _uiEvents.NotifyWorkersChanged();
        _logger.LogInformation("Worker authenticated: {WorkerId}", pending.WorkerId);
    }

    public async Task Ready()
    {
        using var scope = _scopeFactory.CreateScope();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();
        var scheduler = scope.ServiceProvider.GetRequiredService<JobScheduler>();

        workers.SetReady(Context.ConnectionId, true);
        _uiEvents.NotifyWorkersChanged();
        _logger.LogInformation("Worker ready: {ConnectionId}", Context.ConnectionId);

        await scheduler.TryAssignJobAsync(Context.ConnectionId);
    }

    /// <summary>
    /// Called by workers after re-authentication to report their current job state.
    /// Returns the server's understanding of what the worker should be running
    /// and the last log sequence persisted so the worker can replay from there.
    /// </summary>
    public async Task<ReconnectResponse> Reconnect(WorkerReconnect request)
    {
        using var scope = _scopeFactory.CreateScope();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
        var logService = scope.ServiceProvider.GetRequiredService<LogStreamService>();

        var worker = workers.GetByConnectionId(Context.ConnectionId);
        if (worker == null)
        {
            throw new HubException("Worker not authenticated. Call Connect and Authenticate first.");
        }

        var runningJob = await jobs.GetRunningJobForWorkerAsync(worker.Id);

        if (runningJob != null)
        {
            if (request.RunningJobId == runningJob)
            {
                // Worker still has the job — all good, it will continue
                _logger.LogInformation("Worker {WorkerId} reattached with running job {JobId}",
                    worker.Id, runningJob);
            }
            else
            {
                // Worker lost the job — fail it
                _logger.LogWarning(
                    "Worker {WorkerId} reconnected without job {JobId}, marking failed",
                    worker.Id, runningJob);
                await jobs.UpdateStatusAsync(runningJob.Value, JobStatus.Failed);
                await logService.NotifyJobCompletedAsync(runningJob.Value, JobStatus.Failed);

                var notifications = scope.ServiceProvider.GetRequiredService<NotificationRepository>();
                await notifications.CreateForActiveSubscribersAsync(runningJob.Value, "JobCompleted");
            }
        }
        else if (request.RunningJobId != null)
        {
            // Worker thinks it's running a job, but the server has no Running record.
            // It was cancelled/failed while disconnected, or is unknown. ExpectedJobId = null
            // tells the worker to abort — log it for visibility.
            _logger.LogInformation(
                "Worker {WorkerId} reports running job {JobId} which server does not consider active",
                worker.Id, request.RunningJobId.Value);
        }

        return new ReconnectResponse
        {
            ExpectedJobId = runningJob
        };
    }

    public async Task JobStarted(Guid jobId)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();

        _logger.LogInformation("Job started: {JobId}", jobId);
        await jobs.UpdateStatusAsync(jobId, JobStatus.Running);
    }

    public async Task ReportCommit(Guid jobId, string commitSha)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();

        _logger.LogInformation("Job {JobId} resolved to commit {CommitSha}", jobId, commitSha);
        await jobs.SetCommitAsync(jobId, commitSha);
        _uiEvents.NotifyJobsChanged();
    }

    public async Task StreamLogs(LogChunk chunk)
    {
        using var scope = _scopeFactory.CreateScope();
        var logService = scope.ServiceProvider.GetRequiredService<LogStreamService>();

        await logService.ProcessChunkAsync(chunk);
    }

    public async Task JobCompleted(Guid jobId, JobResult result)
    {
        using var scope = _scopeFactory.CreateScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<JobScheduler>();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
        var logService = scope.ServiceProvider.GetRequiredService<LogStreamService>();

        var worker = workers.GetByConnectionId(Context.ConnectionId);
        if (worker == null)
        {
            _logger.LogWarning(
                "Rejected JobCompleted for {JobId} from unauthenticated connection {ConnectionId}",
                jobId, Context.ConnectionId);
            throw new HubException("Worker not authenticated.");
        }

        // A worker runs one job at a time. A JobCompleted naming anything other than
        // its current assignment is a stale or confused message — acting on it would
        // mark the wrong job complete and free a still-busy worker for more work.
        var runningJob = await jobs.GetRunningJobForWorkerAsync(worker.Id);
        if (runningJob != null && runningJob != jobId)
        {
            _logger.LogWarning(
                "Worker {WorkerId} reported JobCompleted for {JobId} but its running job is {RunningJobId}; ignoring",
                worker.Id, jobId, runningJob.Value);
            return;
        }

        _logger.LogInformation("Job completed: {JobId} - {Status}", jobId, result.Status);
        await scheduler.CompleteJobAsync(jobId, result);
        await logService.NotifyJobCompletedAsync(jobId, result.Status);
        workers.SetReady(Context.ConnectionId, true);

        if (result.Workspaces != null)
            workers.SetWorkspaces(Context.ConnectionId, result.Workspaces);

        await scheduler.TryAssignJobAsync(Context.ConnectionId);
    }

    public Task WorkspaceDeleted(string name, DeleteWorkspaceResult result)
    {
        using var scope = _scopeFactory.CreateScope();
        var deletionService = scope.ServiceProvider.GetRequiredService<WorkspaceDeletionService>();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

        deletionService.Complete(Context.ConnectionId, name, result);

        if (result.Success)
            workers.RemoveWorkspace(Context.ConnectionId, name);

        return Task.CompletedTask;
    }

    public Task ReportDiagnostic(DiagnosticReport report)
    {
        using var scope = _scopeFactory.CreateScope();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

        // Reject reports from connections that haven't authenticated. The connection
        // mapping is set in Authenticate(); without it we'd accept reports from any
        // bare SignalR caller.
        var worker = workers.GetByConnectionId(Context.ConnectionId);
        if (worker == null)
        {
            _logger.LogWarning(
                "Rejected ReportDiagnostic from unauthenticated connection {ConnectionId}",
                Context.ConnectionId);
            throw new HubException("Worker not authenticated.");
        }

        workers.SetDiagnostic(worker.Id, report);
        _uiEvents.NotifyWorkersChanged();
        _logger.LogInformation("Worker {WorkerId} diagnostic: {Status} - {Summary}",
            worker.Id, report.Status, report.Summary);
        return Task.CompletedTask;
    }

    public async Task Heartbeat(WorkerHeartbeat status)
    {
        using var scope = _scopeFactory.CreateScope();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

        await workers.UpdateHeartbeatAsync(Context.ConnectionId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _pendingAuths.TryRemove(Context.ConnectionId, out _);

        using var scope = _scopeFactory.CreateScope();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
        var logService = scope.ServiceProvider.GetRequiredService<LogStreamService>();

        var worker = workers.GetByConnectionId(Context.ConnectionId);
        if (worker != null)
        {
            _logger.LogInformation("Worker disconnected: {WorkerId}", worker.Id);

            // Check if the worker had a running job — it stays Running,
            // awaiting the worker to reconnect and resume or report completion.
            var runningJob = await jobs.GetRunningJobForWorkerAsync(worker.Id);
            if (runningJob != null)
            {
                // Flush any buffered logs to DB so they're persisted
                await logService.FlushJobAsync(runningJob.Value);
                _logger.LogWarning(
                    "Worker {WorkerId} disconnected with running job {JobId}, awaiting reconnection",
                    worker.Id, runningJob.Value);
            }
        }

        await workers.SetDisconnectedAsync(Context.ConnectionId);
        _uiEvents.NotifyWorkersChanged();
        await base.OnDisconnectedAsync(exception);
    }
}

internal class PendingAuth
{
    public required Guid WorkerId { get; init; }
    public required byte[] WorkerNonce { get; init; }
    public required byte[] ServerNonce { get; init; }
    public required DateTime CreatedAt { get; init; }
}
