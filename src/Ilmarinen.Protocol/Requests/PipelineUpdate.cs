using NUlid;

namespace Ilmarinen.Protocol.Requests;

public record PipelineUpdate
{
    public string? Name { get; init; }
    public Ulid? RepositoryId { get; init; }
    public string? Ref { get; init; }
    public string? ScriptPath { get; init; }

    public string? Schedule { get; init; }
    public bool ClearSchedule { get; init; }
}
