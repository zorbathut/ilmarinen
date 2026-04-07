using System;

namespace Ilmarinen.Database.Entities;

public class Subscriber
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; }
    public DateTime? LastHeartbeat { get; set; }
    public int HeartbeatTimeoutMinutes { get; set; } = 5;
    public DateTime CreatedAt { get; set; }
}
