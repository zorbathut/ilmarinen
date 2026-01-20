namespace Ilmarinen.Protocol.Requests;

/// <summary>
/// CLI → Server: Submit a new job via HTTP POST /api/jobs
/// </summary>
public record JobSubmission
{
    public required string RepoUrl { get; init; }
    public required string Ref { get; init; }
    public required string ScriptPath { get; init; }
    public string? GitToken { get; init; }
}
