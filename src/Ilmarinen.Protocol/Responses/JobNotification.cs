using NUlid;

namespace Ilmarinen.Protocol.Responses;

public record JobNotification
{
    public required Ulid NotificationId { get; init; }
    public required string EventType { get; init; }
    public required Ulid JobId { get; init; }
    public required JobStatus Status { get; init; }
    public required string RepoUrl { get; init; }
    public required string Ref { get; init; }
    public required string ScriptPath { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public string? PipelineName { get; init; }
}
