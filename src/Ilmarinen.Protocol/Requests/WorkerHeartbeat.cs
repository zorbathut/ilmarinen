using System;

namespace Ilmarinen.Protocol.Requests;

/// <summary>
/// Worker → Server: Heartbeat via SignalR Hub.Heartbeat()
/// </summary>
public record WorkerHeartbeat
{
    public required Guid WorkerId { get; init; }
}
