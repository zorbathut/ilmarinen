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

    // Updated on job assignment/completion
    public Ulid? CurrentJobId { get; set; }
    public DateTime LastSeen { get; set; }
}
