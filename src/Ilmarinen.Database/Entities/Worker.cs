using System;

namespace Ilmarinen.Database.Entities;

public class Worker
{
    // Set at registration, immutable
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public required byte[] PublicKey { get; set; }
    public DateTime RegisteredAt { get; set; }

    public DateTime LastSeen { get; set; }
}
