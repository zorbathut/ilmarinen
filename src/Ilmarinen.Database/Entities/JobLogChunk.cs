using NUlid;
using System;

namespace Ilmarinen.Database.Entities;

public class JobLogChunk
{
    public required Ulid Id { get; set; }
    public required Ulid JobId { get; set; }
    public required int SequenceNumber { get; set; }
    public required string Content { get; set; }
    public required DateTime Timestamp { get; set; }

    public Job? Job { get; set; }
}
