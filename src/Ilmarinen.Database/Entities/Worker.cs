using NUlid;
using System;

namespace Ilmarinen.Database.Entities;

public class Worker
{
    // Set at registration, immutable
    public required Ulid Id { get; set; }
    public required string Name { get; set; }
    public required byte[] PublicKey { get; set; }
    public DateTime RegisteredAt { get; set; }

    // Updated on connect/disconnect/heartbeat
    public bool IsConnected { get; set; }
    public bool IsReady { get; set; }
    public Ulid? CurrentJobId { get; set; }
    public DateTime LastSeen { get; set; }
}
