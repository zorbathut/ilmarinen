using Ilmarinen.Database.Entities;
using Ilmarinen.Database;
using Ilmarinen.Protocol.Responses;
using Microsoft.EntityFrameworkCore;
using NUlid;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Server.Services;

public class NotificationRepository
{
    private readonly IlmarinenDbContext _db;

    public NotificationRepository(IlmarinenDbContext db)
    {
        _db = db;
    }

    public async Task CreateForActiveSubscribersAsync(Ulid jobId, string eventType)
    {
        var now = DateTime.UtcNow;
        var activeIds = await _db.Subscribers
            .Where(s => s.IsActive && s.LastHeartbeat != null
                && s.LastHeartbeat.Value.AddMinutes(s.HeartbeatTimeoutMinutes) > now)
            .Select(s => s.Id)
            .ToListAsync();

        foreach (var subscriberId in activeIds)
        {
            _db.Notifications.Add(new Notification
            {
                Id = Ulid.NewUlid(),
                SubscriberId = subscriberId,
                JobId = jobId,
                EventType = eventType,
                CreatedAt = now
            });
        }

        if (activeIds.Count > 0)
            await _db.SaveChangesAsync();
    }

    public async Task<List<JobNotification>> PullAsync(Ulid subscriberId, int limit = 10)
    {
        var now = DateTime.UtcNow;
        var lockUntil = now.AddMinutes(5);

        var notifications = await _db.Notifications
            .Include(n => n.Job)
            .Where(n => n.SubscriberId == subscriberId
                && !n.IsProcessed
                && (n.LockedUntil == null || n.LockedUntil <= now))
            .OrderBy(n => n.CreatedAt)
            .Take(limit)
            .ToListAsync();

        // Lock them
        foreach (var n in notifications)
        {
            n.LockedUntil = lockUntil;
            n.RetryCount++;
        }

        if (notifications.Count > 0)
            await _db.SaveChangesAsync();

        // Resolve pipeline names
        var pipelineIds = notifications
            .Where(n => n.Job.PipelineId != null)
            .Select(n => n.Job.PipelineId!.Value)
            .Distinct()
            .ToList();

        var pipelineNames = pipelineIds.Count > 0
            ? await _db.Pipelines
                .Where(p => pipelineIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Name)
            : new Dictionary<Ulid, string>();

        return notifications.Select(n => new JobNotification
        {
            NotificationId = n.Id,
            EventType = n.EventType,
            JobId = n.JobId,
            Status = n.Job.Status,
            RepoUrl = n.Job.RepoUrl,
            Ref = n.Job.Ref,
            ScriptPath = n.Job.ScriptPath,
            CreatedAt = n.Job.CreatedAt,
            StartedAt = n.Job.StartedAt,
            CompletedAt = n.Job.CompletedAt,
            PipelineName = n.Job.PipelineId != null
                && pipelineNames.TryGetValue(n.Job.PipelineId.Value, out var name) ? name : null
        }).ToList();
    }

    public async Task AcknowledgeAsync(IReadOnlyList<Ulid> notificationIds)
    {
        await _db.Notifications
            .Where(n => notificationIds.Contains(n.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.IsProcessed, true)
                .SetProperty(n => n.LockedUntil, (DateTime?)null));
    }

    public async Task UnlockStaleAsync()
    {
        var now = DateTime.UtcNow;
        await _db.Notifications
            .Where(n => !n.IsProcessed && n.LockedUntil != null && n.LockedUntil <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.LockedUntil, (DateTime?)null));
    }

    public async Task DeleteOldProcessedAsync(TimeSpan retention)
    {
        var cutoff = DateTime.UtcNow - retention;
        await _db.Notifications
            .Where(n => n.IsProcessed && n.CreatedAt < cutoff)
            .ExecuteDeleteAsync();
    }
}
