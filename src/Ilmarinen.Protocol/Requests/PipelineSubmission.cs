namespace Ilmarinen.Protocol.Requests;

public record PipelineSubmission
{
    public required string Name { get; init; }
    public required string RepoUrl { get; init; }
    public required string Ref { get; init; }
    public required string ScriptPath { get; init; }
    public string? GitToken { get; init; }
}
