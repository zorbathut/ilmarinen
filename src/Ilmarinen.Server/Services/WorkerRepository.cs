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
    private readonly WorkerSessions _sessions;

    // Diagnostic state, keyed by worker ID (not connection ID) so it survives reconnects. Cleared on revoke, not disconnect — a reconnecting worker re-pushes its cached diagnostic so the value here is replaced anyway, and keeping it across the brief reconnect window avoids a "no diagnostic" UI flicker.
    private readonly ConcurrentDictionary<Guid, DiagnosticReport> _workerDiagnostics = new();

    // Latest workspace disk usage, keyed by worker ID for the same reason as diagnostics: a reconnect shouldn't blank every size until the worker's next measurement lands. The connection-keyed workspace list still decides which workspaces are shown.
    private readonly ConcurrentDictionary<Guid, WorkspaceUsageReport> _workerUsage = new();

    public WorkerRepository(IServiceScopeFactory scopeFactory, WorkerSessions sessions)
    {
        _scopeFactory = scopeFactory;
        _sessions = sessions;
    }

    /// <summary>Records an authenticated connection as the worker's session (see <see cref="WorkerSessions.Start"/>).</summary>
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

        _sessions.Start(connectionId, workerId);
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
        // _workerDiagnostics is intentionally NOT cleared here — keyed on worker ID, it survives reconnects, and the worker re-pushes its cached report on auth.
        var session = _sessions.End(connectionId);
        if (session != null)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

            var worker = await db.Workers.FirstOrDefaultAsync(w => w.Id == session.WorkerId);
            if (worker != null)
            {
                worker.LastSeen = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
        }
    }

    public void SetReady(string connectionId, bool isReady)
    {
        var session = _sessions.ByConnection(connectionId);
        if (session != null)
        {
            session.Ready = isReady;
        }
    }

    public async Task UpdateHeartbeatAsync(string connectionId)
    {
        if (_sessions.ByConnection(connectionId) is { } session)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

            var worker = await db.Workers.FirstOrDefaultAsync(w => w.Id == session.WorkerId);
            if (worker != null)
            {
                worker.LastSeen = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
        }
    }

    public ConnectedWorker? GetByConnectionId(string connectionId)
    {
        var session = _sessions.ByConnection(connectionId);
        if (session == null)
        {
            return null;
        }

        return new ConnectedWorker
        {
            Id = session.WorkerId,
            ConnectionId = connectionId,
            IsReady = session.Ready,
        };
    }

    /// <summary>
    /// The ready workers' connection IDs and priorities, highest priority first. Ties are ordered arbitrarily. Callers must still re-validate each candidate before assigning: the in-memory ready flag can lag the database, which is the source of truth for whether a worker is busy.
    /// </summary>
    public async Task<IReadOnlyList<(string ConnectionId, WorkerPriority Priority)>> GetReadyWorkersByPriorityAsync()
    {
        var candidates = _sessions.All()
            .Where(s => s.Ready)
            .ToDictionary(s => s.WorkerId, s => s.ConnectionId);

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
        var connectedIds = _sessions.All().Select(s => s.WorkerId).ToList();
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
        var session = _sessions.ByConnection(connectionId);
        if (session != null && bundleHash != null)
        {
            session.BundleHash = bundleHash;
        }
    }

    public string? GetBundleHash(string connectionId)
    {
        return _sessions.ByConnection(connectionId)?.BundleHash;
    }

    public void SetWorkspaces(string connectionId, IReadOnlyList<WorkspaceInfo>? workspaces)
    {
        var session = _sessions.ByConnection(connectionId);
        if (session != null && workspaces != null)
        {
            session.Workspaces = workspaces.ToImmutableList();
        }
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

    public void SetWorkspaceUsage(Guid workerId, WorkspaceUsageReport report)
    {
        _workerUsage[workerId] = report;
    }

    public void ClearWorkspaceUsage(Guid workerId)
    {
        _workerUsage.TryRemove(workerId, out _);
    }

    public void RemoveWorkspace(string connectionId, string name)
    {
        var session = _sessions.ByConnection(connectionId);
        if (session != null)
        {
            ImmutableInterlocked.Update(ref session.Workspaces, list => list.RemoveAll(ws => ws.Name == name));
        }
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
        return _sessions.ByWorker(workerId)?.ConnectionId;
    }

    public async Task<WorkerView?> GetByIdAsync(Guid id)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

        var w = await db.Workers.FirstOrDefaultAsync(x => x.Id == id);
        if (w == null) return null;

        var session = _sessions.ByWorker(w.Id);
        _workerDiagnostics.TryGetValue(w.Id, out var diagnostic);
        _workerUsage.TryGetValue(w.Id, out var usage);

        var currentJobIds = await db.Jobs
            .Where(j => j.WorkerId == id && j.Status == JobStatus.Running)
            .Select(j => j.Id)
            .ToListAsync();

        // A worker holding a Running job is never ready, even if the in-memory flag still says so — the flag can lag reality until the next assignment attempt.
        var isReady = session is { Ready: true } && currentJobIds.Count == 0;

        return new WorkerView
        {
            Id = w.Id,
            Name = w.Name,
            IsConnected = session != null,
            IsReady = isReady,
            CurrentJobs = currentJobIds,
            RegisteredAt = w.RegisteredAt,
            LastSeen = w.LastSeen,
            LastIpAddress = w.LastIpAddress,
            Priority = w.Priority,
            Workspaces = WorkspaceView.Join(session?.Workspaces ?? [], usage),
            MirrorsSizeBytes = usage?.MirrorsSizeBytes,
            UsageMeasured = usage != null,
            Diagnostic = diagnostic,
            BundleHash = session?.BundleHash
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
            var session = _sessions.ByWorker(w.Id);
            var isReady = session is { Ready: true } && !workerToJobs[w.Id].Any();
            _workerDiagnostics.TryGetValue(w.Id, out var diagnostic);
            _workerUsage.TryGetValue(w.Id, out var usage);

            return new WorkerView
            {
                Id = w.Id,
                Name = w.Name,
                IsConnected = session != null,
                IsReady = isReady,
                CurrentJobs = workerToJobs[w.Id].ToList(),
                RegisteredAt = w.RegisteredAt,
                LastSeen = w.LastSeen,
                LastIpAddress = w.LastIpAddress,
                Priority = w.Priority,
                Workspaces = WorkspaceView.Join(session?.Workspaces ?? [], usage),
                MirrorsSizeBytes = usage?.MirrorsSizeBytes,
                UsageMeasured = usage != null,
                Diagnostic = diagnostic,
                BundleHash = session?.BundleHash
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
    public IReadOnlyList<WorkspaceView> Workspaces { get; init; } = [];

    /// <summary>
    /// Disk space the worker's git mirrors take. Null when not yet measured or unmeasurable.
    /// </summary>
    public long? MirrorsSizeBytes { get; init; }

    /// <summary>
    /// Whether any disk-usage measurement from the worker has arrived yet.
    /// </summary>
    public bool UsageMeasured { get; init; }

    /// <summary>
    /// A worker with no mirrors yet has nothing to show; one whose mirrors couldn't be measured still shows that.
    /// </summary>
    public bool ShowsMirrors
    {
        get { return UsageMeasured && MirrorsSizeBytes != 0; }
    }
    public DiagnosticReport? Diagnostic { get; init; }
    public string? BundleHash { get; init; }
}

/// <summary>
/// A workspace as the UI shows it: the worker's latest listing joined with its latest disk-usage measurement, which arrives separately and can lag behind.
/// </summary>
public record WorkspaceView
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required DateTimeOffset LastUsed { get; init; }

    /// <summary>
    /// False until a measurement that covers this workspace has arrived.
    /// </summary>
    public bool Measured { get; init; }

    /// <summary>
    /// Disk space deleting the workspace would free. Null when not measured, or when measuring it failed.
    /// </summary>
    public long? SizeBytes { get; init; }

    public static IReadOnlyList<WorkspaceView> Join(IReadOnlyList<WorkspaceInfo> workspaces, WorkspaceUsageReport? usage)
    {
        return workspaces.Select(ws =>
        {
            long? size = null;
            var measured = usage != null && usage.WorkspaceSizes.TryGetValue(ws.Name, out size);
            return new WorkspaceView
            {
                Name = ws.Name,
                Path = ws.Path,
                LastUsed = ws.LastUsed,
                Measured = measured,
                SizeBytes = size
            };
        }).ToList();
    }
}
