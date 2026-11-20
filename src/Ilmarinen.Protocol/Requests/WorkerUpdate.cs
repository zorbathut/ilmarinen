namespace Ilmarinen.Protocol.Requests;

public record WorkerUpdate
{
    /// <summary>A worker priority's name.</summary>
    public required string Priority { get; init; }
}
