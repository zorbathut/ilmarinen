using System;

namespace Ilmarinen.Protocol.Requests;

/// <summary>
/// Worker → Server: Stream log output via SignalR Hub.StreamLogs()
/// </summary>
public record LogChunk
{
    public required Guid JobId { get; init; }
    public required int SequenceNumber { get; init; }
    public required string Content { get; init; }
    public required DateTime Timestamp { get; init; }

    /// <summary>The assignment whose run wrote this, so the server can tell a punted run's late output from its rerun's. Null from a worker before protocol v2, whose output counts as the job's current run's.</summary>
    public Guid? AssignmentId { get; init; }
}
