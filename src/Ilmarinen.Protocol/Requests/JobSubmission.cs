using System.ComponentModel.DataAnnotations;
using System;

namespace Ilmarinen.Protocol.Requests;

/// <summary>
/// CLI → Server: Submit a new job via HTTP POST /api/jobs
/// or internally from pipeline triggers.
///
/// When PipelineId is set, RepoUrl/Ref/ScriptPath/MinWorkerPriority fall back to the pipeline's values.
/// Without a pipeline, MinWorkerPriority falls back to Low: any worker may run the job.
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
    [EnumDataType(typeof(WorkerPriority))]
    public WorkerPriority? MinWorkerPriority { get; init; }
}
