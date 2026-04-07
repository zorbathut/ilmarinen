using System;

namespace Ilmarinen.Database.Entities;

public class JobLogChunk
{
    public required Guid Id { get; set; }
    public required Guid JobId { get; set; }
    public required int SequenceNumber { get; set; }
    public required string Content { get; set; }
    public required DateTime Timestamp { get; set; }

    public Job? Job { get; set; }
}
