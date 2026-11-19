using Ilmarinen.Protocol;
using System;

namespace Ilmarinen.Database.Entities;

public class Job
{
    public required Guid Id { get; set; }
    public required JobStatus Status { get; set; }
    public required string RepoUrl { get; set; }
    public required string Ref { get; set; }
    public string? Commit { get; set; }
    public required string ScriptPath { get; set; }
    public Guid? WorkerId { get; set; }

    // Name the current handing of the job to WorkerId: which one (so a stale outcome can't touch it) and which process of that worker it went to (so a reconnect can tell a job that never arrived from one that may have partly run). Cleared when the job is queued again.
    public Guid? AssignmentId { get; set; }
    public Guid? WorkerInstanceId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? EncryptedGitToken { get; set; }
    public GitTokenMode GitTokenMode { get; set; }
    public Guid? PipelineId { get; set; }
    public WorkerPriority MinWorkerPriority { get; set; }
    public JobPriority Priority { get; set; }
}
