using Ilmarinen.Worker.Metrics;
using NUnit.Framework;
using System.Linq;
using System;

namespace Ilmarinen.Core.Tests.Metrics;

/// <summary>The /proc files the host figures come from, in text captured from a real host, and the figures between two readings.</summary>
[TestFixture]
public class HostStatsTests
{
    private const string Stat = """
        cpu  133497911 145101086 34255141 919651608 42272593 4912017 2764059 0 675562 2448478
        cpu0 3323823 3406886 976594 29866967 1915254 303024 467784 0 6559 7388
        intr 123456 0 0
        ctxt 987654
        procs_running 12
        procs_blocked 3
        """;

    private const string MemInfo = """
        MemTotal:       64961496 kB
        MemFree:        20298676 kB
        MemAvailable:   26330312 kB
        Cached:          7854024 kB
        SwapTotal:      134217724 kB
        SwapFree:       86542276 kB
        Dirty:             70964 kB
        HugePages_Total:       0
        """;

    private const string DiskStats = """
         259       0 nvme0n1 167809421 22522467 7899731921 283584768 93676353 58488662 11369042130 3601998497 0 69411125 3916943606 5413030 0 18664913104 20402318 2524386 10958023
         259       1 nvme0n1p1 321 1052 12311 185 2 0 2 0 0 100 190 3 0 612520 5 0 0
         259       2 nvme0n1p2 167808993 22521415 7899716370 283584568 93676343 58488662 11369042128 3601997089 0 69060140 3905983971 5413027 0 18664300584 20402312 0 0
           7       0 loop0 5670 0 26278 354 0 0 0 0 0 271 354 0 0 0 0 0 0
           8       0 sda 100 0 2000 0 50 0 4000 0 0 500 0 0 0 0 0 0 0
           8       1 sda1 100 0 2000 0 50 0 4000 0 0 500 0 0 0 0 0 0 0
         253       0 dm-0 9 0 9 0 9 0 9 0 0 9 0 0 0 0 0 0 0
        """;

    private const string MemoryPressure = """
        some avg10=0.05 avg60=0.20 avg300=0.11 total=3785879600
        full avg10=0.04 avg60=0.20 avg300=0.11 total=3357164712
        """;

    [TestCase("nvme0n1", true)]
    [TestCase("sda", true)]
    [TestCase("vdb", true)]
    [TestCase("xvda", true)]
    [TestCase("mmcblk0", true)]
    [TestCase("nvme0n1p2", false)]
    [TestCase("sda1", false)]
    [TestCase("loop0", false)]
    [TestCase("dm-0", false)]
    [TestCase("md0", false)]
    [TestCase("zram0", false)]
    [TestCase("ram0", false)]
    public void IsWholeDisk_CountsOnlyDisksIOReaches(string name, bool whole)
    {
        Assert.That(HostStats.IsWholeDisk(name), Is.EqualTo(whole));
    }

    [Test]
    public void ParseStat_ReadsTheAggregateCpuLine_ProcessCounts_AndHowManyCpus()
    {
        var (cpu, running, blocked, cpus) = HostStats.ParseStat(Stat);

        Assert.That(cpu, Is.EqualTo(new CpuTicks(133497911, 145101086, 34255141, 919651608, 42272593, 4912017, 2764059, 0)));
        Assert.That(running, Is.EqualTo(12));
        Assert.That(blocked, Is.EqualTo(3));
        Assert.That(cpus, Is.EqualTo(1), "one cpuN line in the capture");
    }

    [Test]
    public void ParseMemInfo_ReadsKilobyteFields()
    {
        var mem = HostStats.ParseMemInfo(MemInfo);

        Assert.That(mem["MemAvailable"], Is.EqualTo(26330312));
        Assert.That(mem["Dirty"], Is.EqualTo(70964));
        Assert.That(mem["HugePages_Total"], Is.Zero);
    }

    [Test]
    public void ParseDiskStats_KeepsWholeDisksOnly()
    {
        var disks = HostStats.ParseDiskStats(DiskStats);

        Assert.That(disks.Select(d => d.Name), Is.EqualTo(new[] { "nvme0n1", "sda" }));
        Assert.That(disks[0], Is.EqualTo(new DiskCounters("nvme0n1", 167809421, 7899731921, 93676353, 11369042130, 69411125)));
    }

    [Test]
    public void ParsePressure_ReadsTenSecondAverages()
    {
        Assert.That(HostStats.ParsePressure(MemoryPressure), Is.EqualTo(new Pressure(0.05, 0.04)));
        Assert.That(HostStats.ParsePressure("some avg10=1.16 avg60=1.99 avg300=1.78 total=17959289459\n"), Is.EqualTo(new Pressure(1.16, null)), "cpu pressure has no full line on older kernels");
        Assert.That(HostStats.ParsePressure(""), Is.Null);
    }

    private static HostSnapshot Snapshot(DateTimeOffset time, CpuTicks cpu, DiskCounters disk)
    {
        return new HostSnapshot(time, cpu, 4, 1, HostStats.ParseMemInfo(MemInfo), [disk], new Pressure(1, null), new Pressure(2, 3), new Pressure(4, 5));
    }

    [Test]
    public void Between_TurnsTwoReadingsIntoCoresAndRates()
    {
        var start = DateTimeOffset.Parse("2026-11-19T12:00:00Z");
        var before = Snapshot(start, new CpuTicks(1000, 0, 500, 0, 100, 0, 0, 0), new DiskCounters("sda", 10, 100, 20, 200, 1000));
        var after = Snapshot(start.AddSeconds(2), new CpuTicks(1600, 0, 700, 0, 140, 0, 0, 0), new DiskCounters("sda", 30, 2100, 60, 4200, 2500));

        var figures = HostStats.Between(before, after);

        Assert.That(figures.CpuUser, Is.EqualTo(3.0).Within(1e-9), "600 ticks of 1/100 s over 2 s");
        Assert.That(figures.CpuSystem, Is.EqualTo(1.0).Within(1e-9));
        Assert.That(figures.CpuIowait, Is.EqualTo(0.2).Within(1e-9));
        Assert.That(figures.DiskReadBytesPerSec, Is.EqualTo(2000 * 512 / 2.0).Within(1e-9));
        Assert.That(figures.DiskWriteOpsPerSec, Is.EqualTo(20).Within(1e-9));
        Assert.That(figures.DiskBusy, Is.EqualTo(0.75).Within(1e-9));
        Assert.That(figures.MemAvailableBytes, Is.EqualTo(26330312L * 1024));
        Assert.That(figures.SwapUsedBytes, Is.EqualTo((134217724L - 86542276L) * 1024));
        Assert.That(figures.PsiMemFull, Is.EqualTo(3));
    }

    [Test]
    public void Between_WithNoEarlierReading_HasGaugesButNoRates()
    {
        var now = Snapshot(DateTimeOffset.UtcNow, new CpuTicks(1, 1, 1, 1, 1, 1, 1, 1), new DiskCounters("sda", 1, 1, 1, 1, 1));

        var figures = HostStats.Between(null, now);

        Assert.That(figures.CpuUser, Is.Null);
        Assert.That(figures.DiskReadBytesPerSec, Is.Null);
        Assert.That(figures.ProcsRunning, Is.EqualTo(4));
        Assert.That(figures.MemFreeBytes, Is.EqualTo(20298676L * 1024));
    }
}
