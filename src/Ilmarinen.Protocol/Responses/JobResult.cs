using System.Collections.Generic;
using System;

namespace Ilmarinen.Protocol.Responses;

/// <summary>
/// Worker → Server: Report job result via SignalR Hub.JobCompleted()
/// </summary>
public record JobResult
{
    public required Guid Id { get; init; }
    public required JobStatus Status { get; init; }
    public TimeSpan Duration { get; init; }
    public IReadOnlyList<WorkspaceInfo>? Workspaces { get; init; }
}
