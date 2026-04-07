using System.Collections.Generic;
using System;

namespace Ilmarinen.Protocol.Responses;

/// <summary>
/// Server → CLI: Job details via HTTP GET /api/jobs/{id}
/// </summary>
public record JobInfo
{
    public required Guid Id { get; init; }
    public required JobStatus Status { get; init; }
    public required string RepoUrl { get; init; }
    public required string Ref { get; init; }
    public string? Commit { get; init; }
    public required string ScriptPath { get; init; }
    public Guid? WorkerId { get; init; }
    public string? WorkerName { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public IReadOnlyList<ArtifactInfo>? Artifacts { get; init; }
    public GitTokenMode GitTokenMode { get; init; }
    public Guid? PipelineId { get; init; }
    public string? PipelineName { get; init; }
    public bool CanRetry { get; init; }
    public string? RetryBlockedReason { get; init; }
}
