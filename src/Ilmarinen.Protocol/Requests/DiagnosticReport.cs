using System.Collections.Generic;
using System;

namespace Ilmarinen.Protocol.Requests;

/// <summary>
/// Worker → Server: result of a diagnostic check.
/// Sent at worker startup and after on-demand re-checks; cached on the server
/// to expose worker health in the UI without re-running the check.
/// </summary>
public record DiagnosticReport
{
    public required DiagnosticStatus Status { get; init; }
    public required string Summary { get; init; }
    public required IReadOnlyList<DiagnosticStepResult> Steps { get; init; }
    public required DateTime CheckedAt { get; init; }
}
