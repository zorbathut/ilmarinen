using Ilmarinen.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUlid;
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
    private readonly ConcurrentDictionary<string, Ulid> _connectionToWorker = new();

    // In-memory set of connection IDs that are ready for work
    private readonly ConcurrentDictionary<string, bool> _readyWorkers = new();

    // In-memory mapping of connection ID to workspace names
    private readonly ConcurrentDictionary<string, ImmutableList<string>> _workerWorkspaces = new();

    public WorkerRepository(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task ConnectAsync(string connectionId, Ulid workerId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

        var worker = await db.Workers.FirstOrDefaultAsync(w => w.Id == workerId)
            ?? throw new InvalidOperationException($"Worker {workerId} not found in database.");

        worker.LastSeen = DateTime.UtcNow;
        await db.SaveChangesAsync();

        _connectionToWorker[connectionId] = workerId;
    }

    public async Task<byte[]?> GetWorkerPublicKeyAsync(Ulid workerId)
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

        if (_connectionToWorker.TryRemove(connectionId, out var workerId))
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

            var worker = await db.Workers.FirstOrDefaultAsync(w => w.Id == workerId);
            if (worker != null)
            {
                worker.CurrentJobId = null;
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

    public async Task SetCurrentJobAsync(string connectionId, Ulid? jobId)
    {
        if (_connectionToWorker.TryGetValue(connectionId, out var workerId))
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

            var worker = await db.Workers.FirstOrDefaultAsync(w => w.Id == workerId);
            if (worker != null)
            {
                worker.CurrentJobId = jobId;
                worker.LastSeen = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
        }

        // Worker is ready when it has no job
        SetReady(connectionId, jobId == null);
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

    public string? FindConnectionIdByJobId(Ulid jobId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

        foreach (var (connectionId, workerId) in _connectionToWorker)
        {
            var worker = db.Workers.FirstOrDefault(w => w.Id == workerId);
            if (worker?.CurrentJobId == jobId)
                return connectionId;
        }
        return null;
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
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

        var workers = await db.Workers
            .OrderByDescending(w => w.LastSeen)
            .ToListAsync();

        return workers.Select(w =>
        {
            var connectionId = FindConnectionIdByWorkerId(w.Id);
            var isConnected = connectionId != null;
            var isReady = connectionId != null && _readyWorkers.ContainsKey(connectionId);

            ImmutableList<string>? workspaces = null;
            if (connectionId != null)
                _workerWorkspaces.TryGetValue(connectionId, out workspaces);

            return new WorkerView
            {
                Id = w.Id,
                Name = w.Name,
                IsConnected = isConnected,
                IsReady = isReady,
                CurrentJobId = w.CurrentJobId,
                RegisteredAt = w.RegisteredAt,
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
}

public class WorkerView
{
    public required Ulid Id { get; init; }
    public required string Name { get; init; }
    public bool IsConnected { get; init; }
    public bool IsReady { get; init; }
    public Ulid? CurrentJobId { get; init; }
    public DateTime RegisteredAt { get; init; }
    public DateTime LastSeen { get; init; }
    public IReadOnlyList<string> Workspaces { get; init; } = [];
}
