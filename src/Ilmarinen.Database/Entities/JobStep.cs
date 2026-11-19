using System;

namespace Ilmarinen.Database.Entities;

/// <summary>
/// One step of a job and what it used, as the worker summarised it when the step ended (Ilmarinen.Protocol.Metrics.StepSummaryRecord), so analysis across jobs needn't parse every stream. Figures are sampled, and cover only the containers on the job's network: work the step did elsewhere (an image build, a pull, a script's own docker run) shows only in the host figures of the stream.
/// </summary>
public class JobStep
{
    public required Guid JobId { get; set; }
    public required int Index { get; set; }
    public required string Name { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }
    public required string Status { get; set; }
    public string? Image { get; set; }
    public bool ImageFromEarlierStep { get; set; }
    public double PullSeconds { get; set; }
    public double CpuSeconds { get; set; }
    public double CpuThrottledSeconds { get; set; }
    public double PeakCores { get; set; }
    public long PeakMemBytes { get; set; }
    public long ReadBytes { get; set; }
    public long WriteBytes { get; set; }

    public Job? Job { get; set; }
}
