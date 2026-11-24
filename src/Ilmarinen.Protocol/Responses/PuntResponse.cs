namespace Ilmarinen.Protocol.Responses;

/// <summary>The answer to POST /api/jobs/{id}/punt, whether the punt was made or refused.</summary>
public record PuntResponse
{
    public required PuntStatus Status { get; init; }

    /// <summary>Where the job would go; null when it isn't running, so there is nothing to forecast.</summary>
    public PuntForecastInfo? Forecast { get; init; }

    /// <summary>Whether the job's worker is connected to be told now; if not, the punt takes effect when it reconnects.</summary>
    public required bool WorkerConnected { get; init; }
}
