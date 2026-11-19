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
    private readonly WorkerBundleService _bundles;

    // Diagnostic state, keyed by worker ID (not connection ID) so it survives reconnects. Cleared on revoke, not disconnect — a reconnecting worker re-pushes its cached diagnostic so the value here is replaced anyway, and keeping it across the brief reconnect window avoids a "no diagnostic" UI flicker.
    private readonly ConcurrentDictionary<Guid, DiagnosticReport> _workerDiagnostics = new();

    // Latest workspace disk usage, keyed by worker ID for the same reason as diagnostics: a reconnect shouldn't blank every size until the worker's next measurement lands. The connection-keyed workspace list still decides which workspaces are shown.
    private readonly ConcurrentDictionary<Guid, WorkspaceUsageReport> _workerUsage = new();

    public WorkerRepository(IServiceScopeFactory scopeFactory, WorkerSessions sessions, WorkerBundleService bundles)
    {
        _scopeFactory = scopeFactory;
        _sessions = sessions;
        _bundles = bundles;
    }

    /// <summary>Records an authenticated handshake in the database, and returns whether the worker owes a diagnostic.</summary>
    public async Task<bool> RecordConnectionAsync(Guid workerId, string? ipAddress)
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
        return worker.DiagnosticOwed;
    }

    /// <summary>Writes whether the worker owes a diagnostic. Caller holds the worker's session lock and updates <see cref="WorkerSessions"/> to match.</summary>
    public async Task SetDiagnosticOwedAsync(Guid workerId, bool owed)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        await db.Workers
            .Where(w => w.Id == workerId)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.DiagnosticOwed, owed));
    }

    public async Task<byte[]?> GetWorkerPublicKeyAsync(Guid workerId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

        var worker = await db.Workers.FirstOrDefaultAsync(w => w.Id == workerId);
        return worker?.PublicKey;
    }

    /// <summary>Records when the worker was last heard from.</summary>
    public async Task TouchLastSeenAsync(Guid workerId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        await db.Workers
            .Where(w => w.Id == workerId)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.LastSeen, DateTime.UtcNow));
    }

    /// <summary>Whether the worker's current session can be handed a job right now, reading the database for its running jobs.</summary>
    public async Task<bool> IsReadyAsync(WorkerSession session)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        var busy = await db.Jobs.AnyAsync(j => j.WorkerId == session.WorkerId && j.Status == JobStatus.Running);
        return IsReady(session, busy);
    }

    private bool IsReady(WorkerSession session, bool hasRunningJob)
    {
        return WorkerReadiness.IsReady(session, _sessions.DiagnosticOwed(session.WorkerId), hasRunningJob, _bundles.CurrentHash);
    }

    /// <summary>
    /// The ready workers and their priorities, highest priority first. Ties are ordered arbitrarily. A snapshot: dispatch re-checks each candidate under its lock before assigning.
    /// </summary>
    public async Task<IReadOnlyList<(Guid WorkerId, WorkerPriority Priority)>> GetReadyWorkersByPriorityAsync()
    {
        var sessions = _sessions.All();
        if (sessions.Count == 0)
        {
            return [];
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

        var workerIds = sessions.Select(s => s.WorkerId).ToList();
        var busy = (await db.Jobs
            .Where(j => j.Status == JobStatus.Running && j.WorkerId != null && workerIds.Contains(j.WorkerId.Value))
            .Select(j => j.WorkerId!.Value)
            .ToListAsync()).ToHashSet();

        var ready = sessions.Where(s => IsReady(s, busy.Contains(s.WorkerId))).Select(s => s.WorkerId).ToList();
        if (ready.Count == 0)
        {
            return [];
        }

        var ordered = await db.Workers
            .Where(w => ready.Contains(w.Id))
            .OrderByDescending(w => w.Priority)
            .Select(w => new { w.Id, w.Priority })
            .ToListAsync();

        return ordered.Select(w => (w.Id, w.Priority)).ToList();
    }

    /// <summary>
    /// The workers that can take work now or soon: connected, and either ready or running a job (the definition on UnsatisfiableJobsReport). The ready half is dispatch's own candidate list, so the two can't disagree about which workers are ready.
    /// </summary>
    public async Task<IReadOnlyList<AvailableWorker>> GetAvailableWorkersAsync()
    {
        var connectedIds = _sessions.All().Select(s => s.WorkerId).ToList();
        if (connectedIds.Count == 0)
        {
            return [];
        }

        var ready = await GetReadyWorkersByPriorityAsync();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

        // Joined through the workers table, like the ready candidates, so a revoked worker still holding a connection doesn't count.
        var busy = await db.Workers
            .Where(w => connectedIds.Contains(w.Id) && db.Jobs.Any(j => j.WorkerId == w.Id && j.Status == JobStatus.Running))
            .Select(w => new { w.Id, w.Priority })
            .ToListAsync();

        return ready.Select(r => new AvailableWorker(r.WorkerId, r.Priority))
            .Concat(busy.Where(b => !ready.Any(r => r.WorkerId == b.Id)).Select(b => new AvailableWorker(b.Id, b.Priority)))
            .ToList();
    }

    /// <summary>Workers with a session, joined through the workers table like <see cref="GetAvailableWorkersAsync"/>, so a revoked worker still holding a connection doesn't count.</summary>
    public async Task<int> CountConnectedWorkersAsync()
    {
        var connectedIds = _sessions.All().Select(s => s.WorkerId).ToList();
        if (connectedIds.Count == 0)
        {
            return 0;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        return await db.Workers.CountAsync(w => connectedIds.Contains(w.Id));
    }

    /// <summary>The highest priority among available workers (<see cref="GetAvailableWorkersAsync"/>), or null when there are none.</summary>
    public async Task<WorkerPriority?> GetHighestAvailablePriorityAsync()
    {
        var available = await GetAvailableWorkersAsync();
        return available.Count == 0 ? null : available.Max(w => w.Priority);
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

    /// <summary>Drops everything held in memory about a revoked worker. Its connection, if any, is fenced from then on: every call it makes is refused.</summary>
    public async Task ForgetAsync(Guid workerId)
    {
        using (await _sessions.LockAsync(workerId))
        {
            _sessions.Forget(workerId);
        }
        _workerDiagnostics.TryRemove(workerId, out _);
        _workerUsage.TryRemove(workerId, out _);
    }

    public void SetWorkspaceUsage(Guid workerId, WorkspaceUsageReport report)
    {
        _workerUsage[workerId] = report;
    }

    public void RemoveWorkspace(string connectionId, string name)
    {
        var session = _sessions.ByConnection(connectionId);
        if (session != null)
        {
            ImmutableInterlocked.Update(ref session.Workspaces, list => list.RemoveAll(ws => ws.Name == name));
        }
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

        var isReady = session != null && IsReady(session, currentJobIds.Count > 0);

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
            var isReady = session != null && IsReady(session, workerToJobs[w.Id].Any());
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

public record AvailableWorker(Guid WorkerId, WorkerPriority Priority);

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
