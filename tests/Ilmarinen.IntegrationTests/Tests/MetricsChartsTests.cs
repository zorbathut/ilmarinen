using Ilmarinen.Protocol.Metrics;
using Ilmarinen.Server.Components;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>The job page's resource charts as layout: scaled lines, step markers, shared-host bands and thinning, all from records.</summary>
[TestFixture]
public class MetricsChartsTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-19T12:00:00Z");

    private static SampleRecord Sample(double seconds, double hostCpu, double jobCpu, int cotenants = 0)
    {
        return new SampleRecord
        {
            Ts = T0.AddSeconds(seconds),
            IntervalSeconds = 2,
            Phase = "pipeline",
            Cotenants = cotenants,
            Host = new HostFigures { CpuUser = hostCpu, CpuSystem = 0, MemAvailableBytes = 1L << 30 },
            Containers = [new ContainerFigures { Id = "step", Role = "step", CpuCores = jobCpu, MemBytes = 1 << 20 }, new ContainerFigures { Id = "worker", Role = "worker", CpuCores = 100, MemBytes = 1L << 40 }]
        };
    }

    private static readonly List<MetricsRecord> Records =
    [
        new HostRecord { Ts = T0, Hostname = "h", CpuCount = 4, MemTotalBytes = 4L << 30 },
        Sample(0, 1, 0),
        new StepRecord { Ts = T0.AddSeconds(5), Index = 0, Name = "build", Event = "start" },
        Sample(5, 4, 3, cotenants: 1),
        Sample(10, 2, 1),
    ];

    [Test]
    public void NoSamples_NoCharts()
    {
        Assert.That(MetricsCharts.Build([new HostRecord { Ts = T0, Hostname = "h", CpuCount = 1 }]), Is.Empty);
    }

    [Test]
    public void CpuChart_ScalesToItsHighestValue_AndLeavesTheWorkerOutOfTheJobsLine()
    {
        var cpu = MetricsCharts.Build(Records)[0];

        Assert.That(cpu.YMax, Is.EqualTo(4), "the host's peak; the worker's own container isn't the job's");
        Assert.That(cpu.Lines, Has.Count.EqualTo(2));
        Assert.That(cpu.Lines[0].Points, Is.EqualTo("0,90 300,0 600,60"), "4 cores at the top, the time span across the width");
        Assert.That(cpu.Lines[1].Points, Is.EqualTo("0,120 300,30 600,90"));
    }

    [Test]
    public void Markers_AndSharedHostBands_FallWhereTheyHappened()
    {
        var cpu = MetricsCharts.Build(Records)[0];

        Assert.That(cpu.Markers, Is.EqualTo(new[] { new ChartMarker(300, "build") }));
        Assert.That(cpu.SharedHost, Is.EqualTo(new[] { new ChartBand(300, 600) }));
    }

    [Test]
    public void Memory_ComesFromTheHostsTotalLessWhatIsAvailable()
    {
        var memory = MetricsCharts.Build(Records)[1];

        Assert.That(memory.YMax, Is.EqualTo(3).Within(1e-9), "4 GiB total, 1 GiB available");
    }

    [Test]
    public void Downsample_KeepsEachStretchsPeak_AndDropsMissingValues()
    {
        var points = Enumerable.Range(0, 6000).Select(i => (X: i / 10.0, Y: (double?)(i == 3001 ? 99 : 1))).Append((X: 5.0, Y: null)).ToList();

        var thinned = MetricsCharts.Downsample(points);

        Assert.That(thinned, Has.Count.LessThanOrEqualTo(MetricsCharts.MaxPoints));
        Assert.That(thinned.Max(p => p.Y), Is.EqualTo(99), "a spike survives thinning");
    }
}
