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
    private readonly Wake.WakeStatus _wakeStatus;

    // Diagnostic state, keyed by worker ID (not connection ID) so it survives reconnects. Cleared on revoke, not disconnect — a reconnecting worker re-pushes its cached diagnostic so the value here is replaced anyway, and keeping it across the brief reconnect window avoids a "no diagnostic" UI flicker.
    private readonly ConcurrentDictionary<Guid, DiagnosticReport> _workerDiagnostics = new();

    // Latest workspace disk usage, keyed by worker ID for the same reason as diagnostics: a reconnect shouldn't blank every size until the worker's next measurement lands. The connection-keyed workspace list still decides which workspaces are shown.
    private readonly ConcurrentDictionary<Guid, WorkspaceUsageReport> _workerUsage = new();

    // Kept across disconnects, like the usage: the last that is known of a sleeping worker's disk.
    private readonly ConcurrentDictionary<Guid, DiskSpaceReport> _workerDisk = new();

    public WorkerRepository(IServiceScopeFactory scopeFactory, WorkerSessions sessions, WorkerBundleService bundles, Wake.WakeStatus wakeStatus)
    {
        _scopeFactory = scopeFactory;
        _sessions = sessions;
        _bundles = bundles;
        _wakeStatus = wakeStatus;
    }

    /// <summary>Records an authenticated handshake in the database, and returns whether the worker owes a diagnostic.</summary>
    public async Task<bool> RecordConnectionAsync(Guid workerId, string? ipAddress, int protocolVersion)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

        var worker = await db.Workers.FirstOrDefaultAsync(w => w.Id == workerId)
            ?? throw new InvalidOperationException($"Worker {workerId} not found in database.");

        worker.LastSeen = DateTime.UtcNow;
        worker.ProtocolVersion = protocolVersion;

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

    /// <summary>Whether the worker's current session can be handed a job right now, and whether the job should say to take it while someone is using the machine, reading the database for its mode and its running jobs. A worker with no row (revoked) is never ready.</summary>
    public async Task<WorkerReadyCheck> CheckReadyAsync(WorkerSession session)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        var row = await db.Workers
            .Where(w => w.Id == session.WorkerId)
            .Select(w => new { w.Mode, Busy = db.Jobs.Any(j => j.WorkerId == w.Id && j.Status == JobStatus.Running) })
            .FirstOrDefaultAsync();
        return new WorkerReadyCheck(row != null && IsReadyInMode(session, row.Mode, row.Busy), row?.Mode == WorkerMode.EvenWhileInUse);
    }

    /// <summary>Readiness from the worker's stored mode: its pause, and the hold it is judged by (<see cref="WorkerReadiness.EffectiveHold"/>).</summary>
    private bool IsReadyInMode(WorkerSession session, WorkerMode mode, bool hasRunningJob)
    {
        return IsReady(session, EffectiveHold(session, mode), mode == WorkerMode.Paused, hasRunningJob);
    }

    private static WorkerHold? EffectiveHold(WorkerSession session, WorkerMode mode)
    {
        return WorkerReadiness.EffectiveHold(session.State?.Hold, mode, session.ProtocolVersion);
    }

    private bool IsReady(WorkerSession session, WorkerHold? hold, bool paused, bool hasRunningJob)
    {
        return WorkerReadiness.IsReady(session, hold, _sessions.DiagnosticOwed(session.WorkerId), paused, hasRunningJob, _bundles.CurrentHash);
    }

    /// <summary>A connected worker as of one read of the sessions and the database: everything readiness and availability are decided from. Rank is its priority's.</summary>
    private sealed record ConnectedWorker(WorkerSession Session, string Name, int Rank, bool Paused, bool Busy, WorkerHold? Hold);

    /// <summary>Every worker with a session, read through the workers table so a revoked worker still holding a connection never appears.</summary>
    private async Task<IReadOnlyList<ConnectedWorker>> GetConnectedWorkersAsync()
    {
        var sessions = _sessions.All();
        if (sessions.Count == 0)
        {
            return [];
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

        var workerIds = sessions.Select(s => s.WorkerId).ToList();
        var rows = (await db.Workers
            .Where(w => workerIds.Contains(w.Id))
            .Select(w => new { w.Id, w.Name, w.Priority!.Rank, w.Mode, Busy = db.Jobs.Any(j => j.WorkerId == w.Id && j.Status == JobStatus.Running) })
            .ToListAsync()).ToDictionary(w => w.Id);

        return sessions
            .Where(s => rows.ContainsKey(s.WorkerId))
            .Select(s =>
            {
                var row = rows[s.WorkerId];
                return new ConnectedWorker(s, row.Name, row.Rank, row.Mode == WorkerMode.Paused, row.Busy, EffectiveHold(s, row.Mode));
            })
            .ToList();
    }

    private bool IsReady(ConnectedWorker w)
    {
        return IsReady(w.Session, w.Hold, w.Paused, w.Busy);
    }

    /// <summary>Available as UnsatisfiableJobsReport defines it: connected, not paused or held, and either ready or running a job.</summary>
    private bool IsAvailable(ConnectedWorker w)
    {
        return IsReady(w) || (w.Busy && !w.Paused && w.Hold == null);
    }

    /// <summary>
    /// The ready workers, with the ranks of their priorities and the workspaces they hold, highest first. Ties are ordered arbitrarily. A snapshot: dispatch re-checks each candidate under its lock before assigning.
    /// </summary>
    public async Task<IReadOnlyList<DispatchWorker>> GetReadyWorkersByPriorityAsync()
    {
        return (await GetConnectedWorkersAsync())
            .Where(IsReady)
            .OrderByDescending(w => w.Rank)
            .Select(w => new DispatchWorker(w.Rank, DiskOf(w.Session.WorkerId)))
            .ToList();
    }

    /// <summary>The worker as dispatch would see it once the job it runs now is over, or null if it wouldn't be ready even then (offline, paused, held, owing a diagnostic, about to update).</summary>
    public async Task<DispatchWorker?> GetOnceIdleAsync(Guid workerId)
    {
        if (_sessions.ByWorker(workerId) is not { } session)
        {
            return null;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        var row = await db.Workers
            .Where(w => w.Id == workerId)
            .Select(w => new { w.Mode, w.Priority!.Rank })
            .FirstOrDefaultAsync();
        if (row == null || !WorkerReadiness.IsReadyOnceIdle(session, EffectiveHold(session, row.Mode), _sessions.DiagnosticOwed(workerId), row.Mode == WorkerMode.Paused, _bundles.CurrentHash))
        {
            return null;
        }
        return new DispatchWorker(row.Rank, DiskOf(workerId));
    }

    /// <summary>What the server last heard of a worker's disk: its free space, and the workspaces it holds — as listed on its session if it is connected, else as last measured — each with the space deleting it would free.</summary>
    public WorkerDisk DiskOf(Guid workerId)
    {
        _workerUsage.TryGetValue(workerId, out var usage);
        var names = _sessions.ByWorker(workerId) is { } session
            ? session.Workspaces.Select(w => w.Name)
            : usage?.WorkspaceSizes.Keys ?? [];
        var held = names.Distinct().ToDictionary(n => n, n => usage?.WorkspaceSizes.GetValueOrDefault(n));
        return new WorkerDisk(workerId, _workerDisk.GetValueOrDefault(workerId), held);
    }

    /// <summary>The disks of every worker the server has heard about since it started (<see cref="DiskOf"/>), connected or not: what a need is capped by, and what waking a sleeper is judged by.</summary>
    public IReadOnlyList<WorkerDisk> GetFleetDisks()
    {
        return _workerDisk.Keys.Concat(_workerUsage.Keys).Concat(_sessions.All().Select(s => s.WorkerId)).Distinct().Select(DiskOf).ToList();
    }

    /// <summary>
    /// The workers that can take work now or soon: connected, not paused, and either ready or running a job (the definition on UnsatisfiableJobsReport). Decided from the same readiness as dispatch's candidates, so the two can't disagree about which workers are ready.
    /// </summary>
    public async Task<IReadOnlyList<AvailableWorker>> GetAvailableWorkersAsync()
    {
        return (await GetAvailabilityAsync()).Available;
    }

    /// <summary>
    /// The available workers (<see cref="GetAvailableWorkersAsync"/>), and from the same read the workers that only a pause or someone using their machine keeps from being available, with how many workers are connected at all. A worker both paused and in use is in both lists: either alone would still hold it back.
    /// </summary>
    public async Task<WorkerAvailability> GetAvailabilityAsync()
    {
        var connected = await GetConnectedWorkersAsync();
        return new WorkerAvailability(
            connected.Where(IsAvailable).Select(w => new AvailableWorker(w.Session.WorkerId, w.Rank)).ToList(),
            HeldBackOnly(connected.Where(w => w.Paused)),
            HeldBackOnly(connected.Where(w => w.Hold == WorkerHold.InUse)),
            connected.Count);
    }

    /// <summary>Those that would be available without their pause and without someone using their machine; held back for any other reason (stopping, going to sleep), they stay out.</summary>
    private IReadOnlyList<HeldBackWorker> HeldBackOnly(IEnumerable<ConnectedWorker> workers)
    {
        return workers
            .Where(w => IsAvailable(w with { Paused = false, Hold = w.Hold == WorkerHold.InUse ? null : w.Hold }))
            .OrderBy(w => w.Name)
            .Select(w => new HeldBackWorker(w.Name, w.Rank))
            .ToList();
    }

    /// <summary>Every registered worker, connected or not, as waking sees it (<see cref="Wake.WakePlan"/>).</summary>
    public async Task<IReadOnlyList<Wake.WakeWorker>> GetWakeWorkersAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

        var rows = await db.Workers
            .Select(w => new { w.Id, w.Name, w.Priority!.Rank, w.Mode, w.WakeMacAddress, w.LastSeen, Busy = db.Jobs.Any(j => j.WorkerId == w.Id && j.Status == JobStatus.Running) })
            .ToListAsync();

        return rows.Select(w =>
        {
            var session = _sessions.ByWorker(w.Id);
            var lastSeen = new DateTimeOffset(DateTime.SpecifyKind(w.LastSeen, DateTimeKind.Utc));
            return new Wake.WakeWorker(w.Id, w.Name, w.Rank, w.Mode == WorkerMode.Paused, w.WakeMacAddress, lastSeen, session != null, session != null && IsReadyInMode(session, w.Mode, w.Busy), w.Busy);
        }).ToList();
    }

    /// <summary>
    /// Writes the stored priority and nothing else. To change a worker's priority use JobScheduler.SetWorkerPriorityAsync, which keeps the write from racing a dispatch and dispatches afterwards.
    /// </summary>
    public async Task<bool> SetPriorityAsync(Guid workerId, Guid priorityId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

        if (!await db.Priorities.AnyAsync(p => p.Id == priorityId && p.Kind == PriorityKind.Worker))
        {
            throw new InvalidSubmissionException($"There is no worker priority {priorityId}.");
        }

        var rows = await PriorityRepository.WritingReferencesAsync(() => db.Workers
            .Where(w => w.Id == workerId)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.PriorityId, priorityId)));

        return rows > 0;
    }

    /// <summary>
    /// Writes the stored mode and nothing else. To set a worker's mode use JobScheduler.SetWorkerModeAsync, which keeps the write from racing a dispatch and dispatches afterwards.
    /// </summary>
    public async Task<bool> SetModeAsync(Guid workerId, WorkerMode mode)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

        var rows = await db.Workers
            .Where(w => w.Id == workerId)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Mode, mode));

        return rows > 0;
    }

    /// <summary>Stores the MAC address the worker's host is woken by, already normalized (<see cref="Wake.MagicPacket.Normalize"/>), or null for none. False if there is no such worker.</summary>
    public async Task<bool> SetWakeMacAddressAsync(Guid workerId, string? mac)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

        var rows = await db.Workers
            .Where(w => w.Id == workerId)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.WakeMacAddress, mac));

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
        _workerDisk.TryRemove(workerId, out _);
    }

    /// <summary>Less than this either way is drift, not a change worth dispatching or redrawing for.</summary>
    private const long DiskSpaceChangeBytes = 256L * 1024 * 1024;

    /// <summary>Records the worker's latest free space; returns whether it changed by more than drift (<see cref="DiskSpaceChangeBytes"/>) since the last one that did.</summary>
    public bool SetDiskSpace(Guid workerId, DiskSpaceReport report)
    {
        var changed = false;
        _workerDisk.AddOrUpdate(workerId, _ =>
        {
            changed = true;
            return report;
        }, (_, previous) =>
        {
            changed = Math.Abs(report.FreeBytes - previous.FreeBytes) >= DiskSpaceChangeBytes || report.TotalBytes != previous.TotalBytes || report.MinFreeBytes != previous.MinFreeBytes;
            return changed ? report : previous;
        });
        return changed;
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

    public async Task<string?> GetNameAsync(Guid id)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        return await db.Workers.Where(w => w.Id == id).Select(w => w.Name).FirstOrDefaultAsync();
    }

    public async Task<WorkerView?> GetByIdAsync(Guid id)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

        var w = await db.Workers.Include(x => x.Priority).FirstOrDefaultAsync(x => x.Id == id);
        if (w == null) return null;

        var session = _sessions.ByWorker(w.Id);
        _workerDiagnostics.TryGetValue(w.Id, out var diagnostic);
        _workerUsage.TryGetValue(w.Id, out var usage);

        var currentJobIds = await db.Jobs
            .Where(j => j.WorkerId == id && j.Status == JobStatus.Running)
            .Select(j => j.Id)
            .ToListAsync();

        var isReady = session != null && IsReadyInMode(session, w.Mode, currentJobIds.Count > 0);

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
            ProtocolVersion = w.ProtocolVersion,
            Priority = PriorityRepository.ToRef(w.Priority!),
            Mode = w.Mode,
            Hold = session?.State?.Hold,
            HoldWaived = session?.State?.Hold != null && EffectiveHold(session, w.Mode) == null,
            SpeaksTooOldAProtocolToTakeJobsWhileInUse = session != null && session.ProtocolVersion < WorkerReadiness.TakesJobsWhileInUseFromVersion,
            WakeMacAddress = w.WakeMacAddress,
            IsWaking = session == null && _wakeStatus.IsWaking(w.Id),
            WakeFailedAt = session == null ? _wakeStatus.FailedAt(w.Id) : null,
            Workspaces = WorkspaceView.Join(session?.Workspaces ?? [], usage, session?.State?.Workspace),
            MirrorsSizeBytes = usage?.MirrorsSizeBytes,
            UsageMeasured = usage != null,
            DiskSpace = _workerDisk.GetValueOrDefault(id),
            Diagnostic = diagnostic,
            BundleHash = session?.BundleHash
        };
    }

    /// <summary>Every worker, or only those of one priority.</summary>
    public async Task<IReadOnlyList<WorkerView>> GetAllAsync(Guid? priorityId = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

        var workers = await db.Workers
            .Where(w => priorityId == null || w.PriorityId == priorityId)
            .Include(w => w.Priority)
            .OrderBy(w => w.Name)
            .ThenBy(w => w.Id)
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
            var isReady = session != null && IsReadyInMode(session, w.Mode, workerToJobs[w.Id].Any());
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
                ProtocolVersion = w.ProtocolVersion,
                Priority = PriorityRepository.ToRef(w.Priority!),
                Mode = w.Mode,
                Hold = session?.State?.Hold,
                HoldWaived = session?.State?.Hold != null && EffectiveHold(session, w.Mode) == null,
                SpeaksTooOldAProtocolToTakeJobsWhileInUse = session != null && session.ProtocolVersion < WorkerReadiness.TakesJobsWhileInUseFromVersion,
                WakeMacAddress = w.WakeMacAddress,
                IsWaking = session == null && _wakeStatus.IsWaking(w.Id),
                WakeFailedAt = session == null ? _wakeStatus.FailedAt(w.Id) : null,
                Workspaces = WorkspaceView.Join(session?.Workspaces ?? [], usage, session?.State?.Workspace),
                MirrorsSizeBytes = usage?.MirrorsSizeBytes,
                UsageMeasured = usage != null,
                DiskSpace = _workerDisk.GetValueOrDefault(w.Id),
                Diagnostic = diagnostic,
                BundleHash = session?.BundleHash
            };
        }).ToList();
    }
}

/// <summary>Whether a worker can be handed a job now, and whether the job should say to take it although someone may be using its machine (its mode says so).</summary>
public record WorkerReadyCheck(bool Ready, bool TakeWhileInUse);

/// <summary>An available worker, with the rank of its priority.</summary>
public record AvailableWorker(Guid WorkerId, int Rank);

/// <summary>A worker that would be available but for one thing an operator or a person at its machine can undo, with the rank of its priority.</summary>
public record HeldBackWorker(string Name, int Rank);

public record WorkerAvailability(IReadOnlyList<AvailableWorker> Available, IReadOnlyList<HeldBackWorker> PausedButOtherwiseAvailable, IReadOnlyList<HeldBackWorker> InUseButOtherwiseAvailable, int Connected);

public record WorkerView
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public bool IsConnected { get; init; }
    public bool IsReady { get; init; }
    public IReadOnlyList<Guid> CurrentJobs { get; init; } = [];
    public DateTime RegisteredAt { get; init; }
    public DateTime LastSeen { get; init; }
    public string? LastIpAddress { get; init; }

    /// <summary>The worker protocol version the worker last connected with; null if it hasn't connected since versions were recorded.</summary>
    public int? ProtocolVersion { get; init; }

    /// <summary>Behind the server's own protocol version: raising the minimum the server supports past it would lock the worker out.</summary>
    public bool ProtocolBehind
    {
        get { return ProtocolVersion < WorkerProtocol.Version; }
    }
    public required PriorityRef Priority { get; init; }
    public WorkerMode Mode { get; init; }

    public bool IsPaused
    {
        get { return Mode == WorkerMode.Paused; }
    }

    /// <summary>Why the worker says it is taking no new jobs, if it is; the server may not hold it to that (<see cref="HoldWaived"/>).</summary>
    public WorkerHold? Hold { get; init; }

    /// <summary>The worker's <see cref="Hold"/> doesn't hold it back: it is someone using its machine, and its mode says to take jobs even so (<see cref="WorkerReadiness.EffectiveHold"/>).</summary>
    public bool HoldWaived { get; init; }

    /// <summary>Its connection speaks a protocol older than the one that lets a worker take a job while someone is using its machine, so its mode can't waive that hold.</summary>
    public bool SpeaksTooOldAProtocolToTakeJobsWhileInUse { get; init; }

    /// <summary>The MAC address the worker's host is woken by, if it can be.</summary>
    public string? WakeMacAddress { get; init; }

    /// <summary>Asleep, and being woken.</summary>
    public bool IsWaking { get; init; }

    /// <summary>Asleep, and didn't wake when last woken, at this time; not counted on until it is next seen.</summary>
    public DateTimeOffset? WakeFailedAt { get; init; }
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

    /// <summary>The worker's latest report of free space on its workspace filesystem; null until one arrives. Kept while the worker is away, so it may be stale then.</summary>
    public DiskSpaceReport? DiskSpace { get; init; }

    /// <summary>Free space is below the floor the worker keeps.</summary>
    public bool LowOnDisk
    {
        get { return DiskSpace != null && DiskSpace.FreeBytes < DiskSpace.MinFreeBytes; }
    }

    /// <summary>Of the space in use, what the workspaces and mirrors don't account for: the host's own, and on a containerized worker usually Docker's images and build cache too, which deleting workspaces can't free. It includes any space the filesystem reserves for root, which isn't free to the worker either. Null until both a disk report and a usage measurement have arrived.</summary>
    public long? OtherUseBytes
    {
        get
        {
            if (DiskSpace == null || !UsageMeasured)
            {
                return null;
            }
            var workspaces = Workspaces.Sum(w => w.SizeBytes ?? 0) + (MirrorsSizeBytes ?? 0);
            return Math.Max(0, DiskSpace.TotalBytes - DiskSpace.FreeBytes - workspaces);
        }
    }
    public DiagnosticReport? Diagnostic { get; init; }
    public string? BundleHash { get; init; }

    /// <summary>Kept from every queued job it could take by what else is on its disk (<see cref="DispatchPlan.RoomShortfall"/>), while it is ready or asleep and to be woken; null otherwise.</summary>
    public NoRoomWorker? NoRoom { get; init; }
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

    /// <summary>The running job is using it, as the worker last reported; the worker refuses to delete it meanwhile.</summary>
    public bool InUse { get; init; }

    /// <param name="inUse">The workspace the worker's running job is using, if any.</param>
    public static IReadOnlyList<WorkspaceView> Join(IReadOnlyList<WorkspaceInfo> workspaces, WorkspaceUsageReport? usage, string? inUse)
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
                SizeBytes = size,
                InUse = ws.Name == inUse
            };
        }).ToList();
    }
}
