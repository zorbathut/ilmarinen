using System;

namespace Ilmarinen.Database.Entities;

public class Notification
{
    public required Guid Id { get; set; }
    public required Guid SubscriberId { get; set; }
    public required Guid JobId { get; set; }
    public required string EventType { get; set; }
    public bool IsProcessed { get; set; }
    public DateTime? LockedUntil { get; set; }
    public int RetryCount { get; set; }
    public DateTime CreatedAt { get; set; }

    public Subscriber Subscriber { get; set; } = null!;
    public Job Job { get; set; } = null!;
}
