using Ilmarinen.Protocol;

namespace Ilmarinen.Server.Services;

public enum JobUpdateStatus
{
    Updated,
    NotFound,
    NotQueued
}

/// <summary>How a change to a queued job's priorities went; <see cref="Current"/> is the job's status when it was no longer queued.</summary>
public sealed record JobUpdateResult(JobUpdateStatus Status, JobStatus? Current);
