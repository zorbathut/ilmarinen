using NUlid;

namespace Ilmarinen.Protocol.Requests;

/// <summary>
/// Worker → Server: Register worker via SignalR Hub.Register()
/// </summary>
public record WorkerRegister
{
    public required Ulid WorkerId { get; init; }
    public required string BuildId { get; init; }
    public IReadOnlyList<string>? Workspaces { get; init; }
}
