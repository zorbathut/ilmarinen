using Ilmarinen.Database.Entities;
using System;

namespace Ilmarinen.Server.Services;

/// <summary>
/// A step's summary via GET /api/jobs/{id}/steps (JobStep says what the figures cover). Server-side only: a type in the protocol namespaces would change the protocol hash for a read-only report.
/// </summary>
public record JobStepInfo
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required DateTime StartedAt { get; init; }
    public required DateTime EndedAt { get; init; }
    public required string Status { get; init; }
    public string? Image { get; init; }
    public bool ImageFromEarlierStep { get; init; }
    public double PullSeconds { get; init; }
    public double CpuSeconds { get; init; }
    public double CpuThrottledSeconds { get; init; }
    public double PeakCores { get; init; }
    public long PeakMemBytes { get; init; }
    public long ReadBytes { get; init; }
    public long WriteBytes { get; init; }

    public static JobStepInfo From(JobStep step)
    {
        return new JobStepInfo
        {
            Index = step.Index,
            Name = step.Name,
            StartedAt = step.StartedAt,
            EndedAt = step.EndedAt,
            Status = step.Status,
            Image = step.Image,
            ImageFromEarlierStep = step.ImageFromEarlierStep,
            PullSeconds = step.PullSeconds,
            CpuSeconds = step.CpuSeconds,
            CpuThrottledSeconds = step.CpuThrottledSeconds,
            PeakCores = step.PeakCores,
            PeakMemBytes = step.PeakMemBytes,
            ReadBytes = step.ReadBytes,
            WriteBytes = step.WriteBytes
        };
    }
}
