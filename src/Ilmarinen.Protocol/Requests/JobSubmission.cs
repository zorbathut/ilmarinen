using NUlid;

namespace Ilmarinen.Protocol.Requests;

/// <summary>
/// CLI → Server: Submit a new job via HTTP POST /api/jobs
/// or internally from pipeline triggers.
///
/// When PipelineId is set, RepoUrl/Ref/ScriptPath fall back to the pipeline's values.
/// GitTokenMode controls credential resolution:
///   None     — no git token
///   Inherit  — use the pipeline's repository token (requires PipelineId)
///   Explicit — use the provided GitToken value
/// </summary>
public record JobSubmission
{
    public Ulid? PipelineId { get; init; }
    public string? RepoUrl { get; init; }
    public string? Ref { get; init; }
    public string? ScriptPath { get; init; }
    public GitTokenMode GitTokenMode { get; init; }
    public string? GitToken { get; init; }
}
