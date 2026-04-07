using System;

namespace Ilmarinen.Protocol.Responses;

/// <summary>
/// Server → Worker: Assign job via SignalR Client.AssignJob()
/// </summary>
public record JobAssignment
{
    public required Guid Id { get; init; }
    public required string RepoUrl { get; init; }
    public required string Ref { get; init; }
    public required string ScriptPath { get; init; }
    public string? GitToken { get; init; }
}
