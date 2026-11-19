using System;

namespace Ilmarinen.Protocol.Requests;

/// <summary>
/// Worker → Server: a batch of a job's resource-metrics records via SignalR Hub.StreamMetrics(). Content is NDJSON, one Ilmarinen.Protocol.Metrics record per line, headed by the host record.
/// </summary>
public record JobMetricsChunk
{
    public required Guid JobId { get; init; }
    public required int SequenceNumber { get; init; }
    public required string Content { get; init; }
    public required DateTime Timestamp { get; init; }
}
