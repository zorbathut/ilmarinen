using Ilmarinen.Protocol.Metrics;
using System.Collections.Generic;
using System.Linq;
using System;

namespace Ilmarinen.Worker.Metrics;

/// <summary>
/// One step's use, gathered from the samples taken while it runs (StepSummaryRecord says what it covers). Totals are measured from each container's last known total before the step, so a container carried over from an earlier step counts only what it did in this one, and a container that started during the step counts from zero.
/// </summary>
public sealed class StepAccumulator
{
    private readonly StepRecord _start;
    private readonly IReadOnlyDictionary<string, ContainerFigures> _baseline;
    private readonly Dictionary<string, ContainerFigures> _highest = new();
    private readonly Dictionary<string, DateTimeOffset> _pullsStarted = new();
    private double _pullSeconds;
    private double _peakCores;
    private long _peakMem;

    /// <param name="baseline">Each container's latest figures before the step started.</param>
    public StepAccumulator(StepRecord start, IReadOnlyDictionary<string, ContainerFigures> baseline)
    {
        _start = start;
        _baseline = baseline;
    }

    public int Index
    {
        get { return _start.Index; }
    }

    public void Add(SampleRecord sample)
    {
        var working = sample.Containers.Where(c => c.Role != "worker").ToList();
        _peakCores = Math.Max(_peakCores, working.Sum(c => c.CpuCores ?? 0));
        _peakMem = Math.Max(_peakMem, working.Sum(c => c.MemBytes));

        // Totals only rise, so the highest seen is the latest real one.
        foreach (var container in working)
        {
            if (!_highest.TryGetValue(container.Id, out var highest) || container.CpuSecondsTotal >= highest.CpuSecondsTotal)
            {
                _highest[container.Id] = container;
            }
        }
    }

    public void Add(PullRecord pull)
    {
        if (pull.Event == "start")
        {
            _pullsStarted[pull.Image] = pull.Ts;
        }
        else if (_pullsStarted.Remove(pull.Image, out var started))
        {
            _pullSeconds += (pull.Ts - started).TotalSeconds;
        }
    }

    public StepSummaryRecord Finish(StepRecord end)
    {
        double Rise(Func<ContainerFigures, double> total)
        {
            return _highest.Sum(entry => Math.Max(0, total(entry.Value) - (_baseline.TryGetValue(entry.Key, out var before) ? total(before) : 0)));
        }

        return new StepSummaryRecord
        {
            Ts = end.Ts,
            Index = _start.Index,
            Name = _start.Name,
            StartedAt = _start.Ts,
            Status = end.Status ?? "",
            Image = _start.Image,
            ImageFromEarlierStep = _start.ImageFromEarlierStep ?? false,
            PullSeconds = _pullSeconds,
            CpuSeconds = Rise(c => c.CpuSecondsTotal),
            CpuThrottledSeconds = Rise(c => c.CpuThrottledSecondsTotal),
            PeakCores = _peakCores,
            PeakMemBytes = _peakMem,
            ReadBytes = (long)Rise(c => c.ReadBytesTotal),
            WriteBytes = (long)Rise(c => c.WriteBytesTotal)
        };
    }
}
