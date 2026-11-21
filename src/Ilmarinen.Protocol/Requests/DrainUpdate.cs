namespace Ilmarinen.Protocol.Requests;

public record DrainUpdate
{
    public required bool Draining { get; init; }
}
