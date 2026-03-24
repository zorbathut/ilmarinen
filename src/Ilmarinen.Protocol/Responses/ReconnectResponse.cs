using NUlid;

namespace Ilmarinen.Protocol.Responses;

/// <summary>
/// Server → Worker: Response to WorkerReconnect, tells the worker
/// what job the server expects it to be running and the last log
/// sequence persisted so the worker can replay from there.
/// </summary>
public record ReconnectResponse
{
    /// <summary>
    /// The job the server believes this worker should be running, or null.
    /// </summary>
    public Ulid? ExpectedJobId { get; init; }
}
