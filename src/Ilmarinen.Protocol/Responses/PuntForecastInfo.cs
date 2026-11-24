using System;

namespace Ilmarinen.Protocol.Responses;

/// <summary>Where a punted job would go next: GET /api/jobs/{id}/punt, and part of the answer to a punt.</summary>
public record PuntForecastInfo
{
    public required PuntOutcome Outcome { get; init; }

    /// <summary>The worker it goes to, or for ComesBack the one it comes back to; null when it waits.</summary>
    public Guid? WorkerId { get; init; }
    public string? WorkerName { get; init; }

    /// <summary>The fleet is draining, so nothing is handed out (Waits).</summary>
    public bool Draining { get; init; }
}
