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

    // The reverse direction, which is also the rule that a worker has exactly one live connection: authenticating on a new one supersedes whatever it held before.
    private readonly ConcurrentDictionary<Guid, string> _workerToConnection = new();

    // In-memory set of connection IDs that are ready for work
    private readonly ConcurrentDictionary<string, bool> _readyWorkers = new();

    // In-memory mapping of connection ID to workspace details
    private readonly ConcurrentDictionary<string, ImmutableList<WorkspaceInfo>> _workerWorkspaces = new();

    // Diagnostic state, keyed by worker ID (not connection ID) so it survives reconnects. Cleared on revoke, not disconnect — a reconnecting worker re-pushes its cached diagnostic so the value here is replaced anyway, and keeping it across the brief reconnect window avoids a "no diagnostic" UI flicker.
    private readonly ConcurrentDictionary<Guid, DiagnosticReport> _workerDiagnostics = new();

    // Bundle hash reported at authentication by launcher-run workers; absent for classic workers.
    private readonly ConcurrentDictionary<string, string> _connectionBundleHashes = new();

    public WorkerRepository(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Records an authenticated connection, superseding any the worker still held. A connection can be half-open for as
    /// long as the SignalR client timeout: left in the maps, it stays a dispatch candidate, and a job handed to it is
    /// never delivered and never reconciled — the worker is connected, so nothing reconnects to correct it.
    /// </summary>
    public async Task ConnectAsync(string connectionId, Guid workerId, string? ipAddress)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

        var worker = await db.Workers.FirstOrDefaultAsync(w => w.Id == workerId)
            ?? throw new InvalidOperationException($"Worker {workerId} not found in database.");

        worker.LastSeen = DateTime.UtcNow;

        // Keep the previously recorded address if this connection has none — blanking the field for a worker that is connected right now is worse than showing a slightly older address.
        if (ipAddress != null)
        {
            worker.LastIpAddress = ipAddress;
        }

        await db.SaveChangesAsync();

        _connectionToWorker[connectionId] = workerId;

        // One atomic swap that hands back what it replaced, so exactly one connection ends up mapped. The new mapping goes in before the old one comes out, so the worker is never momentarily unmapped. Two connections authenticating at once resolve to whichever lands second rather than to the newer of the two — self-correcting, since the loser's next hub call fails authentication and it re-syncs.
        string? superseded = null;
        _workerToConnection.AddOrUpdate(workerId, connectionId, (_, existing) =>
        {
            superseded = existing;
            return connectionId;
        });

        if (superseded != null && superseded != connectionId)
        {
            ForgetConnection(superseded);
        }
    }

    /// <summary>Drops everything keyed by a connection that is no longer the worker's. Leaves the reverse mapping alone: it already points at whatever replaced this one.</summary>
    private void ForgetConnection(string connectionId)
    {
        _connectionToWorker.TryRemove(connectionId, out _);
        _readyWorkers.TryRemove(connectionId, out _);
        _workerWorkspaces.TryRemove(connectionId, out _);
        _connectionBundleHashes.TryRemove(connectionId, out _);
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
        _connectionBundleHashes.TryRemove(connectionId, out _);
        // _workerDiagnostics is intentionally NOT cleared here — keyed on worker ID, it survives reconnects, and the worker re-pushes its cached report on auth.

        if (_connectionToWorker.TryRemove(connectionId, out var workerId))
        {
            // Only if this connection is still the worker's. A superseded connection disconnects after the one that replaced it has already been recorded, and unmapping the worker there would read as offline while it is connected and working.
            _workerToConnection.TryRemove(new KeyValuePair<Guid, string>(workerId, connectionId));

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

    /// <summary>
    /// The ready workers' connection IDs and priorities, highest priority first. Ties are ordered arbitrarily. Callers must still re-validate each candidate before assigning: the in-memory ready flag can lag the database, which is the source of truth for whether a worker is busy.
    /// </summary>
    public async Task<IReadOnlyList<(string ConnectionId, WorkerPriority Priority)>> GetReadyWorkersByPriorityAsync()
    {
        var candidates = new Dictionary<Guid, string>();
        foreach (var (connectionId, _) in _readyWorkers)
        {
            if (_connectionToWorker.TryGetValue(connectionId, out var workerId))
            {
                candidates[workerId] = connectionId;
            }
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

        var workerIds = candidates.Keys.ToList();
        var ordered = await db.Workers
            .Where(w => workerIds.Contains(w.Id))
            .OrderByDescending(w => w.Priority)
            .Select(w => new { w.Id, w.Priority })
            .ToListAsync();

        return ordered.Select(w => (candidates[w.Id], w.Priority)).ToList();
    }

    /// <summary>
    /// The highest priority among available workers, or null when there are none. What counts as available is defined on UnsatisfiableJobsReport. The ready half is dispatch's own candidate list, so the two can't disagree about which workers are ready.
    /// </summary>
    public async Task<WorkerPriority?> GetHighestAvailablePriorityAsync()
    {
        var connectedIds = _connectionToWorker.Values.Distinct().ToList();
        if (connectedIds.Count == 0)
        {
            return null;
        }

        var ready = await GetReadyWorkersByPriorityAsync();
        WorkerPriority? highestReady = ready.Count > 0 ? ready[0].Priority : null;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

        // Joined through the workers table, like the ready candidates, so a revoked worker still holding a connection doesn't count.
        var highestBusy = await db.Workers
            .Where(w => connectedIds.Contains(w.Id) && db.Jobs.Any(j => j.WorkerId == w.Id && j.Status == JobStatus.Running))
            .MaxAsync(w => (WorkerPriority?)w.Priority);

        if (highestReady == null || highestBusy > highestReady)
        {
            return highestBusy;
        }

        return highestReady;
    }

    /// <summary>
    /// Writes the stored priority and nothing else. To change a worker's priority use JobScheduler.SetWorkerPriorityAsync, which keeps the write from racing a dispatch and dispatches afterwards.
    /// </summary>
    public async Task<bool> SetPriorityAsync(Guid workerId, WorkerPriority priority)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

        var rows = await db.Workers
            .Where(w => w.Id == workerId)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Priority, priority));

        return rows > 0;
    }

    public void SetBundleHash(string connectionId, string? bundleHash)
    {
        if (bundleHash != null)
            _connectionBundleHashes[connectionId] = bundleHash;
    }

    public string? GetBundleHash(string connectionId)
    {
        _connectionBundleHashes.TryGetValue(connectionId, out var hash);
        return hash;
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
        _workerToConnection.TryGetValue(workerId, out var connectionId);
        return connectionId;
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
        string? bundleHash = null;
        if (connectionId != null)
        {
            _workerWorkspaces.TryGetValue(connectionId, out workspaces);
            _connectionBundleHashes.TryGetValue(connectionId, out bundleHash);
        }
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
            LastIpAddress = w.LastIpAddress,
            Priority = w.Priority,
            Workspaces = workspaces ?? [],
            Diagnostic = diagnostic,
            BundleHash = bundleHash
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
            string? bundleHash = null;
            if (connectionId != null)
            {
                _workerWorkspaces.TryGetValue(connectionId, out workspaces);
                _connectionBundleHashes.TryGetValue(connectionId, out bundleHash);
            }
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
                LastIpAddress = w.LastIpAddress,
                Priority = w.Priority,
                Workspaces = workspaces ?? [],
                Diagnostic = diagnostic,
                BundleHash = bundleHash
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
    public string? LastIpAddress { get; init; }
    public WorkerPriority Priority { get; init; }
    public IReadOnlyList<WorkspaceInfo> Workspaces { get; init; } = [];
    public DiagnosticReport? Diagnostic { get; init; }
    public string? BundleHash { get; init; }
}
