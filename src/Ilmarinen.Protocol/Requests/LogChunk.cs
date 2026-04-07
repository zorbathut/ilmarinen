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
}
