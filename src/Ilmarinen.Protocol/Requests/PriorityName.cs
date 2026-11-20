namespace Ilmarinen.Protocol.Requests;

/// <summary>Client → Server: the name for a new priority (POST /api/priorities/{kind}) or a new name for one (PUT /api/priorities/{id}).</summary>
public record PriorityName
{
    public required string Name { get; init; }
}
