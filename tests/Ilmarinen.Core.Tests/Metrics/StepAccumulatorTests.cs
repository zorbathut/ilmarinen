using Ilmarinen.Protocol.Metrics;
using Ilmarinen.Worker.Metrics;
using NUnit.Framework;
using System.Collections.Generic;
using System;

namespace Ilmarinen.Core.Tests.Metrics;

/// <summary>A step's summary from the samples taken during it: what its containers' running totals rose by, its peaks, and its pulls.</summary>
[TestFixture]
public class StepAccumulatorTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-19T12:00:00Z");

    private static ContainerFigures Container(string id, double cpuTotal, double cores, long mem, long readTotal = 0, string role = "other")
    {
        return new ContainerFigures { Id = id, Role = role, CpuSecondsTotal = cpuTotal, CpuCores = cores, MemBytes = mem, ReadBytesTotal = readTotal };
    }

    private static SampleRecord Sample(double seconds, params ContainerFigures[] containers)
    {
        return new SampleRecord { Ts = T0.AddSeconds(seconds), IntervalSeconds = 2, Phase = "pipeline", StepIndex = 0, Containers = containers };
    }

    [Test]
    public void Finish_TakesEachContainersRiseOverTheStep_LeavingTheWorkerOut()
    {
        var start = new StepRecord { Ts = T0, Index = 0, Name = "build", Event = "start", Image = "sdk", ImageFromEarlierStep = true };
        var step = new StepAccumulator(start, new Dictionary<string, ContainerFigures> { ["svc"] = Container("svc", 10, 1, 50) });

        step.Add(new PullRecord { Ts = T0.AddSeconds(1), Image = "sdk", Event = "start" });
        step.Add(new PullRecord { Ts = T0.AddSeconds(4), Image = "sdk", Event = "end" });
        step.Add(Sample(5, Container("worker", 60, 9, 900, role: "worker"), Container("step", 3, 1.5, 100, 1000, "step"), Container("svc", 11, 0.5, 50)));
        step.Add(Sample(7, Container("worker", 70, 9, 900, role: "worker"), Container("step", 7, 2, 300, 5000, "step"), Container("svc", 12, 0.5, 60)));
        // Stopped containers can report a last zero; the highest total is the real one.
        step.Add(Sample(8, Container("step", 0, 0, 0, 0, "step")));
        var summary = step.Finish(new StepRecord { Ts = T0.AddSeconds(9), Index = 0, Name = "build", Event = "end", Status = "Success" });

        Assert.That(summary.CpuSeconds, Is.EqualTo(7 + (12 - 10)).Within(1e-9), "the step container from zero, the service only what it did during the step, the worker not at all");
        Assert.That(summary.PeakCores, Is.EqualTo(2.5).Within(1e-9));
        Assert.That(summary.PeakMemBytes, Is.EqualTo(360));
        Assert.That(summary.ReadBytes, Is.EqualTo(5000));
        Assert.That(summary.PullSeconds, Is.EqualTo(3).Within(1e-9));
        Assert.That(summary.Status, Is.EqualTo("Success"));
        Assert.That(summary.ImageFromEarlierStep, Is.True);
        Assert.That(summary.StartedAt, Is.EqualTo(T0));
    }
}
