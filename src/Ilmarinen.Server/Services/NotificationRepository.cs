using Ilmarinen.Database.Entities;
using Ilmarinen.Database;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Server.Services;

public class NotificationRepository
{
    private readonly IlmarinenDbContext _db;
    private readonly SubscriberRepository _subscribers;

    public NotificationRepository(IlmarinenDbContext db, SubscriberRepository subscribers)
    {
        _db = db;
        _subscribers = subscribers;
    }

    /// <summary>
    /// Announces a job's completion to every active subscriber, unless its pipeline's notification policy says this outcome isn't worth announcing. The policy is read now rather than when the job was created, so muting a pipeline quiets the jobs it already has in flight; a job with no pipeline, or whose pipeline is gone, is announced.
    /// </summary>
    public async Task CreateForCompletionAsync(Guid jobId, JobStatus status)
    {
        var policy = await (
            from job in _db.Jobs
            join pipeline in _db.Pipelines on job.PipelineId equals pipeline.Id
            where job.Id == jobId
            select (NotificationPolicy?)pipeline.NotificationPolicy).FirstOrDefaultAsync();

        if (!PolicyAllows(policy ?? NotificationPolicy.All, status))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var activeIds = await _subscribers.GetActiveSubscriberIdsAsync();

        foreach (var subscriberId in activeIds)
        {
            _db.Notifications.Add(new Notification
            {
                Id = Guid.CreateVersion7(),
                SubscriberId = subscriberId,
                JobId = jobId,
                EventType = "JobCompleted",
                CreatedAt = now
            });
        }

        if (activeIds.Count > 0)
            await _db.SaveChangesAsync();
    }

    private static bool PolicyAllows(NotificationPolicy policy, JobStatus status)
    {
        return policy switch
        {
            NotificationPolicy.All => true,
            NotificationPolicy.FailuresOnly => status == JobStatus.Failed,
            NotificationPolicy.None => false,
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown notification policy")
        };
    }

    public async Task<List<JobNotification>> PullAsync(Guid subscriberId, int limit = 10)
    {
        var now = DateTime.UtcNow;

        // Matched against the stored value to find the rows this pull won, so it is truncated to the database's microsecond precision rather than relying on both conversions rounding alike.
        var lockUntil = now.AddMinutes(5);
        lockUntil = lockUntil.AddTicks(-(lockUntil.Ticks % 10));

        var candidateIds = await _db.Notifications
            .Where(n => n.SubscriberId == subscriberId
                && !n.IsProcessed
                && (n.LockedUntil == null || n.LockedUntil <= now))
            .OrderBy(n => n.CreatedAt)
            .Take(limit)
            .Select(n => n.Id)
            .ToListAsync();

        if (candidateIds.Count == 0)
        {
            return [];
        }

        // Lock before reading anything off the job: the status below is then read after the lock commits, so a completion that finds a notification still unlocked can rely on this pull reporting its status write.
        await _db.Notifications
            .Where(n => candidateIds.Contains(n.Id)
                && !n.IsProcessed
                && (n.LockedUntil == null || n.LockedUntil <= now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.LockedUntil, lockUntil)
                .SetProperty(n => n.RetryCount, n => n.RetryCount + 1));

        var notifications = await _db.Notifications
            .AsNoTracking()
            .Include(n => n.Job)
            .Where(n => candidateIds.Contains(n.Id) && n.LockedUntil == lockUntil)
            .OrderBy(n => n.CreatedAt)
            .ToListAsync();

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
            : new Dictionary<Guid, string>();

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

    public async Task AcknowledgeAsync(IReadOnlyList<Guid> notificationIds)
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
