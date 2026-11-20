using System;

namespace Ilmarinen.Protocol.Responses;

public record PipelineInfo
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required Guid RepositoryId { get; init; }
    public required string RepositoryName { get; init; }
    public required string RepoUrl { get; init; }
    public required string DefaultRef { get; init; }
    public required string ScriptPath { get; init; }
    public bool HasGitToken { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? Schedule { get; init; }
    public DateTime? LastTriggeredAt { get; init; }
    /// <summary>Null when any worker may run the pipeline's jobs.</summary>
    public PriorityRef? MinWorkerPriority { get; init; }
    public required PriorityRef Priority { get; init; }
    public NotificationPolicy NotificationPolicy { get; init; }
}
