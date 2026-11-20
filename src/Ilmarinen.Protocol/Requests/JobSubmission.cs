using System;

namespace Ilmarinen.Protocol.Requests;

/// <summary>
/// CLI → Server: Submit a new job via HTTP POST /api/jobs
/// or internally from pipeline triggers.
///
/// When PipelineId is set, RepoUrl/Ref/ScriptPath/MinWorkerPriority/Priority fall back to the pipeline's values.
/// Priorities are given by name. Without a pipeline, a job has no minimum worker priority (any worker may run it) and the default job priority; a MinWorkerPriority of "any" (PriorityNames.AnyWorker) asks for no minimum even when the pipeline has one.
/// GitTokenMode controls credential resolution:
///   None     — no git token
///   Inherit  — use the pipeline's repository token (requires PipelineId)
///   Explicit — use the provided GitToken value
/// </summary>
public record JobSubmission
{
    public Guid? PipelineId { get; init; }
    public string? RepoUrl { get; init; }
    public string? Ref { get; init; }
    public string? ScriptPath { get; init; }
    public GitTokenMode GitTokenMode { get; init; }
    public string? GitToken { get; init; }
    public string? MinWorkerPriority { get; init; }
    public string? Priority { get; init; }
}
