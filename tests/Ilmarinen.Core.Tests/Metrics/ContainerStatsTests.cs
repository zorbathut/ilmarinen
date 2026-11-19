using Docker.DotNet.Models;
using Ilmarinen.Worker.Metrics;
using NUnit.Framework;
using System.Text.Json;
using System;

namespace Ilmarinen.Core.Tests.Metrics;

/// <summary>Per-container figures from Docker's one-shot stats, read from a response captured off a cgroup v2 host and a v1-shaped variant of it.</summary>
[TestFixture]
public class ContainerStatsTests
{
    // Captured from a real one-shot stats call on cgroup v2 (trimmed to what is read), with a loop device entry added: v2 lists loop devices among the block IO.
    private const string CgroupV2Response = """
        {
          "id": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
          "name": "/fixture",
          "read": "2026-11-19T13:25:09.055801346Z",
          "cpu_stats": {
            "cpu_usage": {
              "total_usage": 2023313000,
              "usage_in_kernelmode": 16779000,
              "usage_in_usermode": 2006533000
            },
            "system_cpu_usage": 12821999350000000,
            "online_cpus": 32,
            "throttling_data": {
              "periods": 0,
              "throttled_periods": 0,
              "throttled_time": 0
            }
          },
          "memory_stats": {
            "usage": 14323712,
            "stats": {
              "anon": 65536,
              "file": 13783040,
              "file_dirty": 4096,
              "file_writeback": 0,
              "inactive_file": 13783040,
              "active_file": 0
            },
            "limit": 66520571904
          },
          "networks": {
            "eth0": {
              "rx_bytes": 2398,
              "rx_packets": 14,
              "rx_errors": 0,
              "rx_dropped": 0,
              "tx_bytes": 126,
              "tx_packets": 3,
              "tx_errors": 0,
              "tx_dropped": 0
            }
          },
          "pids_stats": {
            "current": 1,
            "limit": 76062
          },
          "blkio_stats": {
            "io_service_bytes_recursive": [
              {
                "major": 259,
                "minor": 0,
                "op": "read",
                "value": 151552
              },
              {
                "major": 259,
                "minor": 0,
                "op": "write",
                "value": 20971520
              },
              {
                "major": 7,
                "minor": 2,
                "op": "read",
                "value": 999999
              }
            ],
            "io_serviced_recursive": null
          }
        }
        """;

    private static ContainerStatsResponse V2()
    {
        return JsonSerializer.Deserialize<ContainerStatsResponse>(CgroupV2Response)!;
    }

    /// <summary>The same figures as cgroup v1 names them: capitalised blkio ops and total_inactive_file.</summary>
    private static ContainerStatsResponse V1()
    {
        var json = CgroupV2Response
            .Replace("\"op\": \"read\"", "\"op\": \"Read\"")
            .Replace("\"op\": \"write\"", "\"op\": \"Write\"")
            .Replace("\"inactive_file\"", "\"total_inactive_file\"")
            .Replace("\"file_dirty\"", "\"total_dirty\"");
        return JsonSerializer.Deserialize<ContainerStatsResponse>(json)!;
    }

    [Test]
    public void FromStats_ReadsTheCountersOfACgroupV2Response()
    {
        var counters = ContainerStats.FromStats("abc", V2());

        Assert.That(counters.CpuSeconds, Is.EqualTo(2.023313).Within(1e-9));
        Assert.That(counters.MemBytes, Is.EqualTo(14323712 - 13783040), "working set: usage less the inactive file cache");
        Assert.That(counters.FileDirtyBytes, Is.EqualTo(4096));
        Assert.That(counters.ReadBytes, Is.EqualTo(151552), "the loop device's reads are already counted on the disk behind it");
        Assert.That(counters.WriteBytes, Is.EqualTo(20971520));
        Assert.That(counters.RxBytes, Is.EqualTo(2398));
        Assert.That(counters.TxBytes, Is.EqualTo(126));
        Assert.That(counters.Pids, Is.EqualTo(1));
        Assert.That(counters.Read, Is.EqualTo(DateTimeOffset.Parse("2026-11-19T13:25:09.055801346Z")));
    }

    [Test]
    public void FromStats_ReadsCgroupV1NamesTheSameWay()
    {
        Assert.That(ContainerStats.FromStats("abc", V1()), Is.EqualTo(ContainerStats.FromStats("abc", V2())));
    }

    [Test]
    public void Between_AFirstSample_MeasuresFromTheContainersStart()
    {
        var counters = ContainerStats.FromStats("abc", V2());

        var figures = ContainerStats.Between(null, counters, counters.Read.AddSeconds(-2), "step");

        Assert.That(figures.CpuCores, Is.EqualTo(2.023313 / 2).Within(1e-9), "every counter began at zero when the container started");
        Assert.That(figures.WriteBytesPerSec, Is.EqualTo(20971520 / 2.0).Within(1e-6));
        Assert.That(figures.CpuSecondsTotal, Is.EqualTo(2.023313).Within(1e-9));
    }

    [Test]
    public void Between_AFirstSampleWithNoStartTime_HasNoRates()
    {
        var figures = ContainerStats.Between(null, ContainerStats.FromStats("abc", V2()), null, "step");

        Assert.That(figures.CpuCores, Is.Null);
        Assert.That(figures.ReadBytesPerSec, Is.Null);
        Assert.That(figures.MemBytes, Is.GreaterThan(0), "gauges need no interval");
    }

    [Test]
    public void Between_ConsecutiveSamples_TakeTheRateOverTheirOwnReadTimes()
    {
        var first = ContainerStats.FromStats("abc", V2());
        var second = first with { Read = first.Read.AddSeconds(4), CpuSeconds = first.CpuSeconds + 6, ReadBytes = first.ReadBytes + 4000 };

        var figures = ContainerStats.Between(first, second, first.Read.AddMinutes(-5), "step");

        Assert.That(figures.CpuCores, Is.EqualTo(1.5).Within(1e-9));
        Assert.That(figures.ReadBytesPerSec, Is.EqualTo(1000).Within(1e-9));
    }
}
