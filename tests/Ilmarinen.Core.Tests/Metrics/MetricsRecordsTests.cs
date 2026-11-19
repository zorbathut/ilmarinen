using Ilmarinen.Protocol.Metrics;
using NUnit.Framework;
using System.Text.Json;
using System;

namespace Ilmarinen.Core.Tests.Metrics;

/// <summary>The metrics stream's lines: every record carries its version and kind, and reads back as what was written.</summary>
[TestFixture]
public class MetricsRecordsTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-19T12:00:00Z");

    private static readonly MetricsRecord[] Records =
    [
        new HostRecord { Ts = At, Hostname = "builder", CpuCount = 8, MemTotalBytes = 1 << 30, Disks = ["sda"], Kernel = "6.1" },
        new ContainerRecord { Ts = At, Id = "abc123", Name = "step", Image = "alpine", Role = "step", StartedAt = At },
        new StepRecord { Ts = At, Index = 0, Name = "build", Event = "start", Image = "alpine", ImageFromEarlierStep = false },
        new PullRecord { Ts = At, Image = "alpine", Event = "end" },
        new SampleRecord { Ts = At, IntervalSeconds = 2, Phase = "pipeline", StepIndex = 0, Step = "build", Cotenants = 1, Host = new HostFigures { CpuUser = 1.5 }, Containers = [new ContainerFigures { Id = "abc123", Role = "step", CpuCores = 0.5, CpuSecondsTotal = 3, MemBytes = 100 }] },
        new StepSummaryRecord { Ts = At, Index = 0, Name = "build", StartedAt = At, Status = "Success", Image = "alpine", CpuSeconds = 12.5, PeakCores = 3, PeakMemBytes = 1 << 20 },
    ];

    [TestCaseSource(nameof(Records))]
    public void Serialize_ThenParse_GivesTheSameRecord(MetricsRecord record)
    {
        var line = MetricsRecords.Serialize(record);
        using var document = JsonDocument.Parse(line);

        Assert.That(document.RootElement.GetProperty("v").GetInt32(), Is.EqualTo(MetricsRecords.Version));
        Assert.That(document.RootElement.GetProperty("kind").GetString(), Is.EqualTo(record.Kind));
        Assert.That(line, Does.Not.Contain("\n"), "one record per line");
        Assert.That(JsonSerializer.Serialize(MetricsRecords.Parse(line), record.GetType(), MetricsRecords.Json), Is.EqualTo(line));
    }

    [Test]
    public void Doubles_AreRoundedToFourSignificantFigures_OrToAWholeNumber()
    {
        var figures = new HostFigures { CpuUser = 0.1 + 0.2, DiskReadBytesPerSec = 123456.789, CpuSteal = 0 };
        var line = MetricsRecords.Serialize(new SampleRecord { Ts = At, IntervalSeconds = 2.000123, Phase = "pipeline", Host = figures });

        Assert.That(line, Does.Contain("\"cpuUser\":0.3,"));
        Assert.That(line, Does.Contain("\"diskReadBytesPerSec\":123457"), "a large figure keeps its whole part");
        Assert.That(line, Does.Contain("\"intervalSeconds\":2,"));
        Assert.That(line, Does.Contain("\"cpuSteal\":0"));
    }

    [Test]
    public void Parse_AKindThisVersionDoesntKnow_IsSkipped()
    {
        Assert.That(MetricsRecords.Parse("""{"v":2,"kind":"gpu","ts":"2026-11-19T12:00:00Z"}"""), Is.Null);
    }
}
