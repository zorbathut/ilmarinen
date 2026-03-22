using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using NUlid;
using System.Collections.Concurrent;
using System;

namespace Ilmarinen.Server.Services;

/// <summary>
/// Service for Blazor components to subscribe to real-time log updates.
/// Used internally by server-side Blazor (avoids SignalR client connection issues in Docker).
/// </summary>
public class LogSubscriptionService
{
    private readonly ConcurrentDictionary<Ulid, ConcurrentDictionary<string, LogSubscriber>> _subscribers = new();

    public record LogSubscriber(
        Action<LogBroadcast> OnLogChunk,
        Action<JobStatus> OnJobCompleted);

    public string Subscribe(Ulid jobId, Action<LogBroadcast> onLogChunk, Action<JobStatus> onJobCompleted)
    {
        var subscriberId = Guid.NewGuid().ToString();
        var jobSubscribers = _subscribers.GetOrAdd(jobId, _ => new ConcurrentDictionary<string, LogSubscriber>());
        jobSubscribers[subscriberId] = new LogSubscriber(onLogChunk, onJobCompleted);
        return subscriberId;
    }

    public void Unsubscribe(Ulid jobId, string subscriberId)
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
                catch
                {
                    // Don't let one subscriber failure affect others
                }
            }
        }
    }

    public void NotifyJobCompleted(Ulid jobId, JobStatus status)
    {
        if (_subscribers.TryGetValue(jobId, out var jobSubscribers))
        {
            foreach (var subscriber in jobSubscribers.Values)
            {
                try
                {
                    subscriber.OnJobCompleted(status);
                }
                catch
                {
                    // Don't let one subscriber failure affect others
                }
            }
        }
    }
}
