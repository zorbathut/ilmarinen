using System;

namespace Ilmarinen.Database.Entities;

/// <summary>One chunk of a job's resource-metrics stream, as the worker sent it: NDJSON records (Ilmarinen.Protocol.Metrics). A chunk the worker re-sent after a send that only seemed to fail is stored twice; readers skip repeated sequence numbers.</summary>
public class JobMetricsBatch
{
    public long Id { get; set; }
    public required Guid JobId { get; set; }
    public required int SequenceNumber { get; set; }
    public required string Content { get; set; }
    public DateTime ReceivedAt { get; set; }

    public Job? Job { get; set; }
}
