using Ilmarinen.Database;
using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Server.Services;

public class WorkerRepository
{
    private readonly IServiceScopeFactory _scopeFactory;

    // In-memory mapping of SignalR connection ID to worker ID
    private readonly ConcurrentDictionary<string, Guid> _connectionToWorker = new();

    // In-memory set of connection IDs that are ready for work
    private readonly ConcurrentDictionary<string, bool> _readyWorkers = new();

    // In-memory mapping of connection ID to workspace details
    private readonly ConcurrentDictionary<string, ImmutableList<WorkspaceInfo>> _workerWorkspaces = new();

    // Diagnostic state, keyed by worker ID (not connection ID) so it survives reconnects. Cleared on revoke, not disconnect — a reconnecting worker re-pushes its cached diagnostic so the value here is replaced anyway, and keeping it across the brief reconnect window avoids a "no diagnostic" UI flicker.
    private readonly ConcurrentDictionary<Guid, DiagnosticReport> _workerDiagnostics = new();

    public WorkerRepository(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task ConnectAsync(string connectionId, Guid workerId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

        var worker = await db.Workers.FirstOrDefaultAsync(w => w.Id == workerId)
            ?? throw new InvalidOperationException($"Worker {workerId} not found in database.");

        worker.LastSeen = DateTime.UtcNow;
        await db.SaveChangesAsync();

        _connectionToWorker[connectionId] = workerId;
    }

    public async Task<byte[]?> GetWorkerPublicKeyAsync(Guid workerId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

        var worker = await db.Workers.FirstOrDefaultAsync(w => w.Id == workerId);
        return worker?.PublicKey;
    }

    public async Task SetDisconnectedAsync(string connectionId)
    {
        _workerWorkspaces.TryRemove(connectionId, out _);
        _readyWorkers.TryRemove(connectionId, out _);
        // _workerDiagnostics is intentionally NOT cleared here — keyed on worker ID, it survives reconnects, and the worker re-pushes its cached report on auth.

        if (_connectionToWorker.TryRemove(connectionId, out var workerId))
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

            var worker = await db.Workers.FirstOrDefaultAsync(w => w.Id == workerId);
            if (worker != null)
            {
                worker.LastSeen = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
        }
    }

    public void SetReady(string connectionId, bool isReady)
    {
        if (isReady)
            _readyWorkers[connectionId] = true;
        else
            _readyWorkers.TryRemove(connectionId, out _);
    }

    public async Task UpdateHeartbeatAsync(string connectionId)
    {
        if (_connectionToWorker.TryGetValue(connectionId, out var workerId))
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

            var worker = await db.Workers.FirstOrDefaultAsync(w => w.Id == workerId);
            if (worker != null)
            {
                worker.LastSeen = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
        }
    }

    public ConnectedWorker? GetByConnectionId(string connectionId)
    {
        if (!_connectionToWorker.TryGetValue(connectionId, out var workerId))
            return null;

        return new ConnectedWorker
        {
            Id = workerId,
            ConnectionId = connectionId,
            IsReady = _readyWorkers.ContainsKey(connectionId),
        };
    }

    public string? FindReadyWorkerConnectionId()
    {
        foreach (var (connectionId, _) in _readyWorkers)
        {
            if (_connectionToWorker.ContainsKey(connectionId))
                return connectionId;
        }
        return null;
    }

    public void SetWorkspaces(string connectionId, IReadOnlyList<WorkspaceInfo>? workspaces)
    {
        if (workspaces != null)
            _workerWorkspaces[connectionId] = workspaces.ToImmutableList();
    }

    public void SetDiagnostic(Guid workerId, DiagnosticReport report)
    {
        _workerDiagnostics[workerId] = report;
    }

    public DiagnosticReport? GetDiagnostic(Guid workerId)
    {
        _workerDiagnostics.TryGetValue(workerId, out var report);
        return report;
    }

    public void ClearDiagnostic(Guid workerId)
    {
        _workerDiagnostics.TryRemove(workerId, out _);
    }

    public void RemoveWorkspace(string connectionId, string name)
    {
        _workerWorkspaces.AddOrUpdate(
            connectionId,
            _ => ImmutableList<WorkspaceInfo>.Empty,
            (_, list) => list.RemoveAll(ws => ws.Name == name));
    }

    public string? FindConnectionIdByJobId(Guid jobId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

        var workerId = db.Jobs
            .Where(j => j.Id == jobId && j.Status == JobStatus.Running)
            .Select(j => j.WorkerId)
            .FirstOrDefault();

        if (workerId == null)
            return null;

        return FindConnectionIdByWorkerId(workerId.Value);
    }

    public string? FindConnectionIdByWorkerId(Guid workerId)
    {
        foreach (var (connectionId, id) in _connectionToWorker)
        {
            if (id == workerId)
                return connectionId;
        }
        return null;
    }

    public async Task<WorkerView?> GetByIdAsync(Guid id)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

        var w = await db.Workers.FirstOrDefaultAsync(x => x.Id == id);
        if (w == null) return null;

        var connectionId = FindConnectionIdByWorkerId(w.Id);
        var isConnected = connectionId != null;

        ImmutableList<WorkspaceInfo>? workspaces = null;
        DiagnosticReport? diagnostic = null;
        if (connectionId != null)
            _workerWorkspaces.TryGetValue(connectionId, out workspaces);
        _workerDiagnostics.TryGetValue(w.Id, out diagnostic);

        var currentJobIds = await db.Jobs
            .Where(j => j.WorkerId == id && j.Status == JobStatus.Running)
            .Select(j => j.Id)
            .ToListAsync();

        // A worker holding a Running job is never ready, even if the in-memory flag still says so — the flag can lag reality until the next assignment attempt.
        var isReady = connectionId != null
            && _readyWorkers.ContainsKey(connectionId)
            && currentJobIds.Count == 0;

        return new WorkerView
        {
            Id = w.Id,
            Name = w.Name,
            IsConnected = isConnected,
            IsReady = isReady,
            CurrentJobs = currentJobIds,
            RegisteredAt = w.RegisteredAt,
            LastSeen = w.LastSeen,
            Workspaces = workspaces ?? [],
            Diagnostic = diagnostic
        };
    }

    public async Task<IReadOnlyList<WorkerView>> GetAllAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

        var workers = await db.Workers
            .OrderByDescending(w => w.LastSeen)
            .ToListAsync();

        // Get all currently running jobs to derive CurrentJobs
        var runningJobs = await db.Jobs
            .Where(j => j.Status == JobStatus.Running && j.WorkerId != null)
            .Select(j => new { j.WorkerId, JobId = j.Id })
            .ToListAsync();

        var workerToJobs = runningJobs.ToLookup(j => j.WorkerId!.Value, j => j.JobId);

        return workers.Select(w =>
        {
            var connectionId = FindConnectionIdByWorkerId(w.Id);
            var isConnected = connectionId != null;
            var isReady = connectionId != null
                && _readyWorkers.ContainsKey(connectionId)
                && !workerToJobs[w.Id].Any();

            ImmutableList<WorkspaceInfo>? workspaces = null;
            DiagnosticReport? diagnostic = null;
            if (connectionId != null)
                _workerWorkspaces.TryGetValue(connectionId, out workspaces);
            _workerDiagnostics.TryGetValue(w.Id, out diagnostic);

            return new WorkerView
            {
                Id = w.Id,
                Name = w.Name,
                IsConnected = isConnected,
                IsReady = isReady,
                CurrentJobs = workerToJobs[w.Id].ToList(),
                RegisteredAt = w.RegisteredAt,
                LastSeen = w.LastSeen,
                Workspaces = workspaces ?? [],
                Diagnostic = diagnostic
            };
        }).ToList();
    }
}

public class ConnectedWorker
{
    public required Guid Id { get; init; }
    public required string ConnectionId { get; init; }
    public bool IsReady { get; init; }
}

public class WorkerView
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public bool IsConnected { get; init; }
    public bool IsReady { get; init; }
    public IReadOnlyList<Guid> CurrentJobs { get; init; } = [];
    public DateTime RegisteredAt { get; init; }
    public DateTime LastSeen { get; init; }
    public IReadOnlyList<WorkspaceInfo> Workspaces { get; init; } = [];
    public DiagnosticReport? Diagnostic { get; init; }
}
