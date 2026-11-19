using Ilmarinen.Protocol.Metrics;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System;

namespace Ilmarinen.Worker.Metrics;

/// <summary>Cumulative CPU time across all CPUs, in USER_HZ ticks, from /proc/stat's aggregate line.</summary>
public sealed record CpuTicks(long User, long Nice, long System, long Idle, long Iowait, long Irq, long Softirq, long Steal);

public sealed record DiskCounters(string Name, long ReadsCompleted, long SectorsRead, long WritesCompleted, long SectorsWritten, long IoTicksMs);

/// <summary>A pressure-stall file's 10-second averages; Full is null where the kernel doesn't report one (cpu on older kernels).</summary>
public sealed record Pressure(double Some, double? Full);

/// <summary>Everything read from /proc at one moment. A part the host doesn't offer is null.</summary>
public sealed record HostSnapshot(
    DateTimeOffset Time,
    CpuTicks? Cpu,
    long? ProcsRunning,
    long? ProcsBlocked,
    IReadOnlyDictionary<string, long>? MemInfoKb,
    IReadOnlyList<DiskCounters>? Disks,
    Pressure? CpuPressure,
    Pressure? MemPressure,
    Pressure? IoPressure);

/// <summary>Parsers for the /proc files the host figures come from, and the figures between two snapshots. /proc/stat, meminfo, diskstats and pressure describe the whole host even inside a container.</summary>
public static partial class HostStats
{
    /// <summary>The kernel's USER_HZ, the unit of /proc/stat; 100 on every Linux architecture Ilmarinen runs on.</summary>
    private const double TicksPerSecond = 100;

    private const int SectorBytes = 512;

    /// <summary>Whole disks only: partitions, loop, ram, zram, device-mapper and md devices would count the same IO twice or count none that reaches a disk.</summary>
    [GeneratedRegex(@"^(sd[a-z]+|vd[a-z]+|xvd[a-z]+|hd[a-z]+|nvme\d+n\d+|mmcblk\d+)$")]
    private static partial Regex WholeDisk();

    public static bool IsWholeDisk(string name)
    {
        return WholeDisk().IsMatch(name);
    }

    /// <summary>The aggregate CPU line, the process counts, and how many CPUs the host has: /proc/stat lists every one of them, where a containerised worker's own CPU count is limited to its quota.</summary>
    public static (CpuTicks? Cpu, long? ProcsRunning, long? ProcsBlocked, int CpuCount) ParseStat(string text)
    {
        CpuTicks? cpu = null;
        long? running = null;
        long? blocked = null;
        var cpus = 0;
        foreach (var line in text.Split('\n'))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0)
            {
                continue;
            }
            if (fields[0].Length > 3 && fields[0].StartsWith("cpu", StringComparison.Ordinal) && char.IsAsciiDigit(fields[0][3]))
            {
                cpus++;
                continue;
            }
            switch (fields[0])
            {
                case "cpu" when fields.Length >= 9:
                    cpu = new CpuTicks(Long(fields[1]), Long(fields[2]), Long(fields[3]), Long(fields[4]), Long(fields[5]), Long(fields[6]), Long(fields[7]), Long(fields[8]));
                    break;
                case "procs_running" when fields.Length >= 2:
                    running = Long(fields[1]);
                    break;
                case "procs_blocked" when fields.Length >= 2:
                    blocked = Long(fields[1]);
                    break;
            }
        }
        return (cpu, running, blocked, cpus);
    }

    /// <summary>/proc/meminfo's fields, in kB as the file gives them.</summary>
    public static IReadOnlyDictionary<string, long> ParseMemInfo(string text)
    {
        var values = new Dictionary<string, long>();
        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }
            var fields = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length > 0 && long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                values[line[..colon]] = value;
            }
        }
        return values;
    }

    /// <summary>The whole disks in /proc/diskstats.</summary>
    public static IReadOnlyList<DiskCounters> ParseDiskStats(string text)
    {
        var disks = new List<DiskCounters>();
        foreach (var line in text.Split('\n'))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 13 || !IsWholeDisk(fields[2]))
            {
                continue;
            }
            disks.Add(new DiskCounters(fields[2], Long(fields[3]), Long(fields[5]), Long(fields[7]), Long(fields[9]), Long(fields[12])));
        }
        return disks;
    }

    public static Pressure? ParsePressure(string text)
    {
        double? some = null;
        double? full = null;
        foreach (var line in text.Split('\n'))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var avg10 = fields.FirstOrDefault(f => f.StartsWith("avg10=", StringComparison.Ordinal));
            if (fields.Length == 0 || avg10 == null || !double.TryParse(avg10["avg10=".Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }
            if (fields[0] == "some")
            {
                some = value;
            }
            else if (fields[0] == "full")
            {
                full = value;
            }
        }
        return some is { } s ? new Pressure(s, full) : null;
    }

    /// <summary>
    /// The figures for the interval ending at <paramref name="current"/>. Rates need a previous snapshot and are null without one; gauges (memory, process counts, pressure) come from the current snapshot alone.
    /// </summary>
    public static HostFigures Between(HostSnapshot? previous, HostSnapshot current)
    {
        var seconds = previous != null ? (current.Time - previous.Time).TotalSeconds : 0;
        var rated = previous != null && seconds > 0;

        double? Cores(Func<CpuTicks, long> part)
        {
            if (!rated || previous!.Cpu is not { } before || current.Cpu is not { } after)
            {
                return null;
            }
            return Math.Max(0, part(after) - part(before)) / TicksPerSecond / seconds;
        }

        double? DiskRate(Func<DiskCounters, long> counter, int scale)
        {
            if (!rated || previous!.Disks is not { } before || current.Disks is not { } after)
            {
                return null;
            }
            return Delta(before, after, counter) * scale / seconds;
        }

        double? busiest = null;
        if (rated && previous!.Disks is { } beforeDisks && current.Disks is { } afterDisks)
        {
            foreach (var disk in afterDisks)
            {
                var earlier = beforeDisks.FirstOrDefault(d => d.Name == disk.Name);
                if (earlier != null)
                {
                    var busy = Math.Clamp((disk.IoTicksMs - earlier.IoTicksMs) / 1000.0 / seconds, 0, 1);
                    busiest = Math.Max(busiest ?? 0, busy);
                }
            }
        }

        long? MemBytes(string key)
        {
            return current.MemInfoKb != null && current.MemInfoKb.TryGetValue(key, out var kb) ? kb * 1024 : null;
        }

        long? swapUsed = MemBytes("SwapTotal") is { } total && MemBytes("SwapFree") is { } free ? total - free : null;

        return new HostFigures
        {
            CpuUser = Cores(c => c.User + c.Nice),
            CpuSystem = Cores(c => c.System + c.Irq + c.Softirq),
            CpuIowait = Cores(c => c.Iowait),
            CpuSteal = Cores(c => c.Steal),
            ProcsRunning = current.ProcsRunning,
            ProcsBlocked = current.ProcsBlocked,
            MemAvailableBytes = MemBytes("MemAvailable"),
            MemFreeBytes = MemBytes("MemFree"),
            MemDirtyBytes = MemBytes("Dirty"),
            SwapUsedBytes = swapUsed,
            DiskReadBytesPerSec = DiskRate(d => d.SectorsRead, SectorBytes),
            DiskWriteBytesPerSec = DiskRate(d => d.SectorsWritten, SectorBytes),
            DiskReadOpsPerSec = DiskRate(d => d.ReadsCompleted, 1),
            DiskWriteOpsPerSec = DiskRate(d => d.WritesCompleted, 1),
            DiskBusy = busiest,
            PsiCpuSome = current.CpuPressure?.Some,
            PsiMemSome = current.MemPressure?.Some,
            PsiMemFull = current.MemPressure?.Full,
            PsiIoSome = current.IoPressure?.Some,
            PsiIoFull = current.IoPressure?.Full
        };
    }

    /// <summary>The summed increase of a counter over the disks present both times; a disk that appeared or vanished between them counts for nothing.</summary>
    private static long Delta(IReadOnlyList<DiskCounters> before, IReadOnlyList<DiskCounters> after, Func<DiskCounters, long> counter)
    {
        long total = 0;
        foreach (var disk in after)
        {
            var earlier = before.FirstOrDefault(d => d.Name == disk.Name);
            if (earlier != null)
            {
                total += Math.Max(0, counter(disk) - counter(earlier));
            }
        }
        return total;
    }

    private static long Long(string field)
    {
        return long.Parse(field, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }
}

/// <summary>Reads the host's /proc. A file the host doesn't have (pressure is off on some kernels; not Linux at all) leaves its part null.</summary>
public sealed class ProcHostSource : IHostSource
{
    private readonly string _root;

    public ProcHostSource(string root)
    {
        _root = root;
    }

    public HostSnapshot Read(DateTimeOffset now)
    {
        var (cpu, running, blocked, _) = TryRead("stat") is { } stat ? HostStats.ParseStat(stat) : (null, null, null, 0);
        return new HostSnapshot(
            now,
            cpu,
            running,
            blocked,
            TryRead("meminfo") is { } meminfo ? HostStats.ParseMemInfo(meminfo) : null,
            TryRead("diskstats") is { } diskstats ? HostStats.ParseDiskStats(diskstats) : null,
            TryRead("pressure/cpu") is { } cpuPressure ? HostStats.ParsePressure(cpuPressure) : null,
            TryRead("pressure/memory") is { } memPressure ? HostStats.ParsePressure(memPressure) : null,
            TryRead("pressure/io") is { } ioPressure ? HostStats.ParsePressure(ioPressure) : null);
    }

    public HostRecord Describe(DateTimeOffset now)
    {
        var snapshot = Read(now);
        var cpus = TryRead("stat") is { } stat ? HostStats.ParseStat(stat).CpuCount : 0;
        long? Bytes(string key)
        {
            return snapshot.MemInfoKb != null && snapshot.MemInfoKb.TryGetValue(key, out var kb) ? kb * 1024 : null;
        }

        return new HostRecord
        {
            Ts = now,
            Hostname = Environment.MachineName,
            CpuCount = cpus > 0 ? cpus : Environment.ProcessorCount,
            MemTotalBytes = Bytes("MemTotal"),
            SwapTotalBytes = Bytes("SwapTotal"),
            Disks = snapshot.Disks?.Select(d => d.Name).ToList() ?? [],
            Kernel = TryRead("sys/kernel/osrelease")?.Trim()
        };
    }

    /// <summary>Null when the file isn't there or can't be read: each part of a snapshot is optional, and which ones a host has is reported once per job by the sampler.</summary>
    private string? TryRead(string relative)
    {
        var path = Path.Combine(_root, relative);
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public interface IHostSource
{
    HostSnapshot Read(DateTimeOffset now);
    HostRecord Describe(DateTimeOffset now);
}
