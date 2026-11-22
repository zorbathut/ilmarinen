namespace Ilmarinen.Protocol.Requests;

/// <summary>Client → Server: new priorities for a queued job (PUT /api/jobs/{id}). They decide when and where the job runs, not what it is, so only these can change.</summary>
public record JobUpdate
{
    /// <summary>A job priority's name; null leaves it as it is.</summary>
    public string? Priority { get; init; }

    /// <summary>A worker priority's name, or "any" to clear the minimum; null leaves it as it is.</summary>
    public string? MinWorkerPriority { get; init; }
}
