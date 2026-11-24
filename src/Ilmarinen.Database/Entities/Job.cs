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

    // Added to the sequence numbers of the current run's log chunks, which every run starts from 1: a rerun's log carries on after the runs before it.
    public int LogSequenceBase { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? EncryptedGitToken { get; set; }
    public GitTokenMode GitTokenMode { get; set; }
    public Guid? PipelineId { get; set; }
    // Null when any worker may run the job.
    public Guid? MinWorkerPriorityId { get; set; }
    public Priority? MinWorkerPriority { get; set; }
    public required Guid PriorityId { get; set; }
    public Priority? Priority { get; set; }

    // What the run worked in and the disk it took, as its worker reported (JobDiskUse): what the server learns the script's disk needs from. Null until a worker reports the run finished.
    public WorkspaceKind? WorkspaceKind { get; set; }
    public string? WorkspaceName { get; set; }
    public bool? WorkspaceFresh { get; set; }
    public long? DiskUsedBytes { get; set; }
    public bool? StoppedForDisk { get; set; }
}
