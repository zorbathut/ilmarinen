namespace Ilmarinen.Protocol.Requests;

public record PipelineTrigger
{
    public string? Ref { get; init; }
    /// <summary>Overrides the pipeline's, by name; "any" for no minimum.</summary>
    public string? MinWorkerPriority { get; init; }
    /// <summary>Overrides the pipeline's, by name.</summary>
    public string? Priority { get; init; }
}
