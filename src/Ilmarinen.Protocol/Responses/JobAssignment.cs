using NUlid;

namespace Ilmarinen.Protocol.Responses;

/// <summary>
/// Server → Worker: Assign job via SignalR Client.AssignJob()
/// </summary>
public record JobAssignment
{
    public required Ulid Id { get; init; }
    public required string RepoUrl { get; init; }
    public required string Ref { get; init; }
    public required string ScriptPath { get; init; }
}
