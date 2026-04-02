using NUlid;

namespace Ilmarinen.Protocol.Requests;

public record PipelineSubmission
{
    public required string Name { get; init; }
    public required Ulid RepositoryId { get; init; }
    public required string Ref { get; init; }
    public required string ScriptPath { get; init; }
    public string? Schedule { get; init; }
}
