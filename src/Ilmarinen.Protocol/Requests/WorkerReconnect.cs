using System;

namespace Ilmarinen.Protocol.Requests;

/// <summary>
/// Worker → Server: Sent after re-authentication to report current job state.
/// Called via SignalR Hub.Reconnect() before signaling Ready.
/// </summary>
public record WorkerReconnect
{
    /// <summary>
    /// The job the worker is currently executing, or null if idle.
    /// </summary>
    public Guid? RunningJobId { get; init; }
}
