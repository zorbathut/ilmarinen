using NUlid;

namespace Ilmarinen.Protocol.Requests;

/// <summary>
/// Worker → Server: Report job completion via SignalR Hub.JobCompleted()
/// </summary>
public record JobCompleted
{
    public required Ulid Id { get; init; }
    public required JobStatus Status { get; init; }
    public TimeSpan Duration { get; init; }
    public IReadOnlyList<string>? Workspaces { get; init; }
}
