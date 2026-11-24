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

    /// <summary>The assignment whose run wrote this, so the server can tell a punted run's late output from its rerun's. Null from a worker before protocol v2, whose output counts as the job's current run's.</summary>
    public Guid? AssignmentId { get; init; }
}
