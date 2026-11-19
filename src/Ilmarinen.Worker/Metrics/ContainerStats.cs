using Docker.DotNet.Models;
using Ilmarinen.Protocol.Metrics;
using System.Collections.Generic;
using System.Linq;
using System;

namespace Ilmarinen.Worker.Metrics;

/// <summary>A container's cumulative counters at one moment, from a one-shot Docker stats response.</summary>
public sealed record ContainerCounters(
    string Id,
    DateTimeOffset Read,
    double CpuSeconds,
    double CpuThrottledSeconds,
    long MemBytes,
    long? MemLimitBytes,
    long? FileDirtyBytes,
    long ReadBytes,
    long WriteBytes,
    long RxBytes,
    long TxBytes,
    long? Pids);

public static class ContainerStats
{
    /// <summary>Loop devices are backed by files on a real disk, so their IO is already counted there.</summary>
    private const ulong LoopMajor = 7;

    /// <summary>
    /// The counters in a stats response, read the same way from cgroup v1 and v2: blkio ops are "Read"/"Write" under v1 and "read"/"write" under v2, and the inactive file cache is total_inactive_file under v1 and inactive_file under v2.
    /// </summary>
    public static ContainerCounters FromStats(string id, ContainerStatsResponse stats)
    {
        var memory = stats.MemoryStats;
        var memStats = memory?.Stats ?? new Dictionary<string, ulong>();
        ulong? Stat(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (memStats.TryGetValue(key, out var value))
                {
                    return value;
                }
            }
            return null;
        }

        var usage = (long)(memory?.Usage ?? 0);
        var inactiveFile = (long)(Stat("inactive_file", "total_inactive_file") ?? 0);

        long Blkio(string op)
        {
            return (long)(stats.BlkioStats?.IoServiceBytesRecursive ?? [])
                .Where(e => e.Major != LoopMajor && string.Equals(e.Op, op, StringComparison.OrdinalIgnoreCase))
                .Aggregate(0UL, (sum, e) => sum + e.Value);
        }

        var networks = stats.Networks?.Values ?? [];
        var limit = memory?.Limit ?? 0;
        return new ContainerCounters(
            id,
            new DateTimeOffset(stats.Read.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(stats.Read, DateTimeKind.Utc) : stats.Read.ToUniversalTime()),
            (stats.CPUStats?.CPUUsage?.TotalUsage ?? 0) / 1e9,
            (stats.CPUStats?.ThrottlingData?.ThrottledTime ?? 0) / 1e9,
            Math.Max(0, usage - inactiveFile),
            limit > 0 ? (long)limit : null,
            Stat("file_dirty", "total_dirty") is { } dirty ? (long)dirty : null,
            Blkio("read"),
            Blkio("write"),
            (long)networks.Aggregate(0UL, (sum, n) => sum + n.RxBytes),
            (long)networks.Aggregate(0UL, (sum, n) => sum + n.TxBytes),
            stats.PidsStats is { } pids ? (long)pids.Current : null);
    }

    /// <summary>
    /// A container's figures for the interval ending at <paramref name="current"/>. With no previous sample the interval runs from the container's start, since every counter began at zero then, so a first sample still has rates. A container that starts and stops between two samples is never seen at all, and one that is seen loses whatever it did after its last sample.
    /// </summary>
    public static ContainerFigures Between(ContainerCounters? previous, ContainerCounters current, DateTimeOffset? startedAt, string role)
    {
        var from = previous?.Read ?? startedAt;
        var seconds = from is { } start ? (current.Read - start).TotalSeconds : 0;

        double? Rate(double now, double before)
        {
            return seconds > 0 ? Math.Max(0, now - before) / seconds : null;
        }

        return new ContainerFigures
        {
            Id = current.Id,
            Role = role,
            CpuCores = Rate(current.CpuSeconds, previous?.CpuSeconds ?? 0),
            CpuSecondsTotal = current.CpuSeconds,
            CpuThrottledSecondsTotal = current.CpuThrottledSeconds,
            MemBytes = current.MemBytes,
            MemLimitBytes = current.MemLimitBytes,
            FileDirtyBytes = current.FileDirtyBytes,
            ReadBytesPerSec = Rate(current.ReadBytes, previous?.ReadBytes ?? 0),
            WriteBytesPerSec = Rate(current.WriteBytes, previous?.WriteBytes ?? 0),
            ReadBytesTotal = current.ReadBytes,
            WriteBytesTotal = current.WriteBytes,
            RxBytesPerSec = Rate(current.RxBytes, previous?.RxBytes ?? 0),
            TxBytesPerSec = Rate(current.TxBytes, previous?.TxBytes ?? 0),
            Pids = current.Pids
        };
    }
}
