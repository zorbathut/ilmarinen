using System;

namespace Ilmarinen.Protocol.Requests;

/// <summary>
/// Worker → Server: Initiate connection and request an authentication challenge.
/// </summary>
public record WorkerConnect
{
    public required Guid WorkerId { get; init; }
    public required string ProtocolHash { get; init; }
    public required byte[] Nonce { get; init; }

    // Not `required`, unlike the fields before them: a worker too old to send them must still get as far as the protocol-hash check, which says what is wrong, rather than fail to deserialize.

    /// <summary>Made when the worker process starts. A connection from a different instance supersedes the worker's current one, and the instance it replaced is never accepted again.</summary>
    public Guid InstanceId { get; init; }

    /// <summary>Counts this process's connections from 1. Within an instance, a higher counter supersedes a lower one, so a handshake that arrives late can't take over from the connection that replaced it.</summary>
    public long ConnectionCounter { get; init; }

    /// <summary>
    /// Hash of the worker bundle this worker was launched from, when running under the launcher. Null for classic manually-deployed workers.
    /// </summary>
    public string? BundleHash { get; init; }
}
