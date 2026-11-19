using System;

namespace Ilmarinen.Protocol.Responses;

/// <summary>
/// Server → Worker: Assign job via SignalR Client.AssignJob()
/// </summary>
public record JobAssignment
{
    public required Guid Id { get; init; }

    /// <summary>Names this one handing of the job to a worker, so its outcome can't be confused with that of an earlier or later one.</summary>
    public required Guid AssignmentId { get; init; }
    public required string RepoUrl { get; init; }
    public required string Ref { get; init; }
    public required string ScriptPath { get; init; }
    public string? GitToken { get; init; }
}
