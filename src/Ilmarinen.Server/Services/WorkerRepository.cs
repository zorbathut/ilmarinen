using System.Collections.Concurrent;
using System.Collections.Immutable;
using Ilmarinen.Database;
using Ilmarinen.Database.Entities;
using Ilmarinen.Protocol.Requests;
using Microsoft.EntityFrameworkCore;
using NUlid;

namespace Ilmarinen.Server.Services;

public class WorkerRepository
{
    private readonly IlmarinenDbContext _db;

    // In-memory mapping of SignalR connection ID to worker ID
    private static readonly ConcurrentDictionary<string, Ulid> _connectionToWorker = new();

    // In-memory mapping of connection ID to workspace names
    private static readonly ConcurrentDictionary<string, ImmutableList<string>> _workerWorkspaces = new();

    public WorkerRepository(IlmarinenDbContext db)
    {
        _db = db;
    }

    public async Task<Ulid> RegisterOrUpdateAsync(string connectionId, WorkerRegister info)
    {
        var workerId = info.WorkerId;

        var worker = await _db.Workers.FirstOrDefaultAsync(w => w.Id == workerId);
        var now = DateTime.UtcNow;

        if (worker == null)
        {
            worker = new Worker
            {
                Id = workerId,
                IsConnected = true,
                IsReady = false,
                FirstSeen = now,
                LastSeen = now
            };
            _db.Workers.Add(worker);
        }
        else
        {
            worker.IsConnected = true;
            worker.IsReady = false;
            worker.LastSeen = now;
        }

        await _db.SaveChangesAsync();

        _connectionToWorker[connectionId] = workerId;

        return workerId;
    }

    public async Task SetDisconnectedAsync(string connectionId)
    {
        _workerWorkspaces.TryRemove(connectionId, out _);

        if (_connectionToWorker.TryRemove(connectionId, out var workerId))
        {
            var worker = await _db.Workers.FirstOrDefaultAsync(w => w.Id == workerId);
            if (worker != null)
            {
                worker.IsConnected = false;
                worker.IsReady = false;
                worker.CurrentJobId = null;
                worker.LastSeen = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }
        }
    }

    public async Task SetReadyAsync(string connectionId, bool isReady)
    {
        if (_connectionToWorker.TryGetValue(connectionId, out var workerId))
        {
            var worker = await _db.Workers.FirstOrDefaultAsync(w => w.Id == workerId);
            if (worker != null)
            {
                worker.IsReady = isReady;
                worker.LastSeen = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }
        }
    }

    public async Task SetCurrentJobAsync(string connectionId, Ulid? jobId)
    {
        if (_connectionToWorker.TryGetValue(connectionId, out var workerId))
        {
            var worker = await _db.Workers.FirstOrDefaultAsync(w => w.Id == workerId);
            if (worker != null)
            {
                worker.CurrentJobId = jobId;
                worker.IsReady = jobId == null;
                worker.LastSeen = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }
        }
    }

    public async Task UpdateHeartbeatAsync(string connectionId)
    {
        if (_connectionToWorker.TryGetValue(connectionId, out var workerId))
        {
            var worker = await _db.Workers.FirstOrDefaultAsync(w => w.Id == workerId);
            if (worker != null)
            {
                worker.LastSeen = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }
        }
    }

    public async Task<ConnectedWorker?> GetByConnectionIdAsync(string connectionId)
    {
        if (!_connectionToWorker.TryGetValue(connectionId, out var workerId))
            return null;

        var worker = await _db.Workers.FirstOrDefaultAsync(w => w.Id == workerId);
        if (worker == null)
            return null;

        return new ConnectedWorker
        {
            Id = worker.Id,
            ConnectionId = connectionId,
            IsReady = worker.IsReady,
            CurrentJobId = worker.CurrentJobId
        };
    }

    public async Task<string?> FindReadyWorkerConnectionIdAsync()
    {
        foreach (var (connectionId, workerId) in _connectionToWorker)
        {
            var worker = await _db.Workers.FirstOrDefaultAsync(w => w.Id == workerId);
            if (worker is { IsConnected: true, IsReady: true, CurrentJobId: null })
            {
                return connectionId;
            }
        }
        return null;
    }

    public void SetWorkspaces(string connectionId, IReadOnlyList<string>? workspaces)
    {
        if (workspaces != null)
            _workerWorkspaces[connectionId] = workspaces.ToImmutableList();
    }

    public void RemoveWorkspace(string connectionId, string name)
    {
        _workerWorkspaces.AddOrUpdate(
            connectionId,
            _ => ImmutableList<string>.Empty,
            (_, list) => list.Remove(name));
    }

    public string? FindConnectionIdByWorkerId(Ulid workerId)
    {
        foreach (var (connectionId, id) in _connectionToWorker)
        {
            if (id == workerId)
                return connectionId;
        }
        return null;
    }

    public async Task<IReadOnlyList<WorkerView>> GetAllAsync()
    {
        var workers = await _db.Workers
            .OrderByDescending(w => w.LastSeen)
            .ToListAsync();

        return workers.Select(w =>
        {
            var connectionId = FindConnectionIdByWorkerId(w.Id);
            ImmutableList<string>? workspaces = null;
            if (connectionId != null)
                _workerWorkspaces.TryGetValue(connectionId, out workspaces);

            return new WorkerView
            {
                Id = w.Id,
                IsConnected = w.IsConnected,
                IsReady = w.IsReady,
                CurrentJobId = w.CurrentJobId,
                FirstSeen = w.FirstSeen,
                LastSeen = w.LastSeen,
                Workspaces = workspaces ?? []
            };
        }).ToList();
    }
}

public class ConnectedWorker
{
    public required Ulid Id { get; init; }
    public required string ConnectionId { get; init; }
    public bool IsReady { get; init; }
    public Ulid? CurrentJobId { get; init; }
}

public class WorkerView
{
    public required Ulid Id { get; init; }
    public bool IsConnected { get; init; }
    public bool IsReady { get; init; }
    public Ulid? CurrentJobId { get; init; }
    public DateTime FirstSeen { get; init; }
    public DateTime LastSeen { get; init; }
    public IReadOnlyList<string> Workspaces { get; init; } = [];
}
