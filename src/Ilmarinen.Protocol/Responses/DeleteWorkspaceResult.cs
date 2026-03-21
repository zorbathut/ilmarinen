namespace Ilmarinen.Protocol.Responses;

/// <summary>
/// Worker → Server: Result of a workspace deletion attempt.
/// </summary>
public record DeleteWorkspaceResult
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
}
