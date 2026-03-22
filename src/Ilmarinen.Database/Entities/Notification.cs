using NUlid;
using System;

namespace Ilmarinen.Database.Entities;

public class Notification
{
    public required Ulid Id { get; set; }
    public required Ulid SubscriberId { get; set; }
    public required Ulid JobId { get; set; }
    public required string EventType { get; set; }
    public bool IsProcessed { get; set; }
    public DateTime? LockedUntil { get; set; }
    public int RetryCount { get; set; }
    public DateTime CreatedAt { get; set; }

    public Subscriber Subscriber { get; set; } = null!;
    public Job Job { get; set; } = null!;
}
