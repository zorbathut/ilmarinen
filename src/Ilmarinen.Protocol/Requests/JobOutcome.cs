using Ilmarinen.Protocol.Responses;
using System.Collections.Generic;
using System;

namespace Ilmarinen.Protocol.Requests;

/// <summary>
/// Worker → Server: what became of one assignment. The worker keeps it until the server acknowledges it.
/// </summary>
public record JobOutcome
{
    public required Guid JobId { get; init; }
    public required Guid AssignmentId { get; init; }
    public required JobOutcomeKind Kind { get; init; }

    /// <summary>How the run went; set for Completed.</summary>
    public JobStatus? Status { get; init; }

    public TimeSpan Duration { get; init; }
    public IReadOnlyList<WorkspaceInfo>? Workspaces { get; init; }
}
