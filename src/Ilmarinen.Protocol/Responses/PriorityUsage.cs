namespace Ilmarinen.Protocol.Responses;

/// <summary>How many workers, pipelines and jobs use a priority: as their own priority, or (for a worker priority) as the minimum a worker must have to run them. Finished jobs count.</summary>
public record PriorityUsage
{
    public int Workers { get; init; }
    public int Pipelines { get; init; }
    public int Jobs { get; init; }
    public int PipelinesNeedingIt { get; init; }
    public int JobsNeedingIt { get; init; }
}
