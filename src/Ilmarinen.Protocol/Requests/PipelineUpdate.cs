namespace Ilmarinen.Protocol.Requests;

public record PipelineUpdate
{
    public string? Name { get; init; }
    public string? RepoUrl { get; init; }
    public string? Ref { get; init; }
    public string? ScriptPath { get; init; }

    /// <summary>
    /// null = don't change, empty string = remove token, non-empty = replace token.
    /// </summary>
    public string? GitToken { get; init; }

    /// <summary>
    /// Must be set to true to update the git token (distinguishes null meaning "not provided" from "remove").
    /// </summary>
    public bool UpdateGitToken { get; init; }

    public string? Schedule { get; init; }
    public bool ClearSchedule { get; init; }
}
