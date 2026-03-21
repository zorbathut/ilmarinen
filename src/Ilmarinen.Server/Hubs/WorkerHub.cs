using System.Collections.Concurrent;
using System.Security.Cryptography;
using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Services;
using Microsoft.AspNetCore.SignalR;
using NUlid;

namespace Ilmarinen.Server.Hubs;

public class WorkerHub : Hub<IWorkerClient>
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ServerKeyService _serverKey;
    private readonly ILogger<WorkerHub> _logger;

    private static readonly ConcurrentDictionary<string, PendingAuth> _pendingAuths = new();

    public WorkerHub(IServiceScopeFactory scopeFactory, ServerKeyService serverKey, ILogger<WorkerHub> logger)
    {
        _scopeFactory = scopeFactory;
        _serverKey = serverKey;
        _logger = logger;
    }

    public async Task<AuthChallenge> Connect(WorkerConnect request)
    {
        // Validate build compatibility early
        if (request.BuildId != BuildInfo.GitCommit)
        {
            _logger.LogError(
                "Worker {WorkerId} rejected: build mismatch (worker: {WorkerBuild}, server: {ServerBuild})",
                request.WorkerId, request.BuildId, BuildInfo.GitCommit);

            throw new HubException(
                $"Build mismatch. Worker is '{request.BuildId}', server is '{BuildInfo.GitCommit}'. " +
                "Rebuild both from the same commit.");
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
            ServerSignature = serverSignature
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
        _logger.LogInformation("Worker authenticated: {WorkerId}", pending.WorkerId);
    }

    public async Task Ready()
    {
        using var scope = _scopeFactory.CreateScope();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();
        var scheduler = scope.ServiceProvider.GetRequiredService<JobScheduler>();

        await workers.SetReadyAsync(Context.ConnectionId, true);
        _logger.LogInformation("Worker ready: {ConnectionId}", Context.ConnectionId);

        await scheduler.TryAssignJobAsync(Context.ConnectionId);
    }

    public async Task JobStarted(Ulid jobId)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();

        _logger.LogInformation("Job started: {JobId}", jobId);
        await jobs.UpdateStatusAsync(jobId, JobStatus.Running);
    }

    public async Task StreamLogs(LogChunk chunk)
    {
        using var scope = _scopeFactory.CreateScope();
        var logService = scope.ServiceProvider.GetRequiredService<LogStreamService>();

        await logService.ProcessChunkAsync(chunk);
    }

    public async Task JobCompleted(Ulid jobId, JobCompleted result)
    {
        using var scope = _scopeFactory.CreateScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<JobScheduler>();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();
        var logService = scope.ServiceProvider.GetRequiredService<LogStreamService>();

        _logger.LogInformation("Job completed: {JobId} - {Status}", jobId, result.Status);
        await scheduler.CompleteJobAsync(jobId, result);
        await logService.NotifyJobCompletedAsync(jobId, result.Status);
        await workers.SetCurrentJobAsync(Context.ConnectionId, null);

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

        var worker = await workers.GetByConnectionIdAsync(Context.ConnectionId);
        if (worker != null)
        {
            _logger.LogInformation("Worker disconnected: {WorkerId}", worker.Id);

            if (worker.CurrentJobId.HasValue)
            {
                await logService.NotifyJobCompletedAsync(worker.CurrentJobId.Value, JobStatus.Failed);
                await jobs.UpdateStatusAsync(worker.CurrentJobId.Value, JobStatus.Failed);
            }
        }

        await workers.SetDisconnectedAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}

internal class PendingAuth
{
    public required Ulid WorkerId { get; init; }
    public required byte[] WorkerNonce { get; init; }
    public required byte[] ServerNonce { get; init; }
    public required DateTime CreatedAt { get; init; }
}
