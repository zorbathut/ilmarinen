using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System;

namespace Ilmarinen.Server.Services;

/// <summary>
/// In-process fan-out of a job's log as it arrives, ahead of persistence. Each open live tail
/// (GET /api/jobs/{id}/logs/stream) is one subscriber; callbacks run on the caller's thread, so a
/// subscriber must hand the chunk off rather than do anything slow with it.
/// </summary>
public class LogSubscriptionService
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, LogSubscriber>> _subscribers = new();
    private readonly ILogger<LogSubscriptionService> _logger;

    public LogSubscriptionService(ILogger<LogSubscriptionService> logger)
    {
        _logger = logger;
    }

    public record LogSubscriber(
        Action<LogBroadcast> OnLogChunk,
        Action<JobStatus> OnJobCompleted);

    public string Subscribe(Guid jobId, Action<LogBroadcast> onLogChunk, Action<JobStatus> onJobCompleted)
    {
        var subscriberId = Guid.NewGuid().ToString();
        var jobSubscribers = _subscribers.GetOrAdd(jobId, _ => new ConcurrentDictionary<string, LogSubscriber>());
        jobSubscribers[subscriberId] = new LogSubscriber(onLogChunk, onJobCompleted);
        return subscriberId;
    }

    /// <summary>
    /// Live tails currently watching this job. A subscriber that outlives the connection that made it
    /// is a leak nothing else would show, so it is worth being able to see the count.
    /// </summary>
    public int SubscriberCount(Guid jobId)
    {
        return _subscribers.TryGetValue(jobId, out var jobSubscribers) ? jobSubscribers.Count : 0;
    }

    public void Unsubscribe(Guid jobId, string subscriberId)
    {
        if (_subscribers.TryGetValue(jobId, out var jobSubscribers))
        {
            jobSubscribers.TryRemove(subscriberId, out _);
            if (jobSubscribers.IsEmpty)
            {
                _subscribers.TryRemove(jobId, out _);
            }
        }
    }

    public void NotifyLogChunk(LogBroadcast chunk)
    {
        if (_subscribers.TryGetValue(chunk.JobId, out var jobSubscribers))
        {
            foreach (var subscriber in jobSubscribers.Values)
            {
                try
                {
                    subscriber.OnLogChunk(chunk);
                }
                catch (Exception ex)
                {
                    // Don't let one subscriber failure affect others
                    _logger.LogError(ex, "Log subscriber callback failed for job {JobId}", chunk.JobId);
                }
            }
        }
    }

    public void NotifyJobCompleted(Guid jobId, JobStatus status)
    {
        if (_subscribers.TryGetValue(jobId, out var jobSubscribers))
        {
            foreach (var subscriber in jobSubscribers.Values)
            {
                try
                {
                    subscriber.OnJobCompleted(status);
                }
                catch (Exception ex)
                {
                    // Don't let one subscriber failure affect others
                    _logger.LogError(ex, "Job-completed subscriber callback failed for job {JobId}", jobId);
                }
            }
        }
    }
}
