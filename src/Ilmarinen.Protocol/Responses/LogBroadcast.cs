using NUlid;

namespace Ilmarinen.Protocol.Responses;

/// <summary>
/// Server → UI: Broadcast log chunk to subscribed clients via SignalR
/// </summary>
public record LogBroadcast
{
    public required Ulid JobId { get; init; }
    public required int SequenceNumber { get; init; }
    public required string Content { get; init; }
    public required DateTime Timestamp { get; init; }
}
