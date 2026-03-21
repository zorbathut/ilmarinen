using NUlid;

namespace Ilmarinen.Protocol.Responses;

public record PipelineInfo
{
    public required Ulid Id { get; init; }
    public required string Name { get; init; }
    public required string RepoUrl { get; init; }
    public required string DefaultRef { get; init; }
    public required string ScriptPath { get; init; }
    public bool HasGitToken { get; init; }
    public DateTime CreatedAt { get; init; }
}
