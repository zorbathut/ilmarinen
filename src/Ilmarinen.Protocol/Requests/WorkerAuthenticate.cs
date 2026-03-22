using System.Collections.Generic;

namespace Ilmarinen.Protocol.Requests;

/// <summary>
/// Worker → Server: Authenticate with signed challenge response.
/// </summary>
public record WorkerAuthenticate
{
    public required byte[] Signature { get; init; }
    public IReadOnlyList<string>? Workspaces { get; init; }
}
