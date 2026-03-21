using NUlid;

namespace Ilmarinen.Protocol.Responses;

/// <summary>
/// Worker → Server: Report job result via SignalR Hub.JobCompleted()
/// </summary>
public record JobResult
{
    public required Ulid Id { get; init; }
    public required JobStatus Status { get; init; }
    public TimeSpan Duration { get; init; }
    public IReadOnlyList<string>? Workspaces { get; init; }
}
