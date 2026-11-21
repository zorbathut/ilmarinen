using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Text.Json;
using System;

namespace Ilmarinen.Protocol.Metrics;

/// <summary>
/// The records of a job's resource-metrics stream: one JSON object per line, each with a version and a kind, so the stream can grow fields without a migration or a protocol change. Carried as text inside JobMetricsChunk, which is why these records stay out of the worker protocol's shape (WorkerProtocol).
///
/// Figures cover the whole host and the containers on the job's network (plus the worker's own container). Work the build does elsewhere — BuildKit image builds, image pulls, a script's own docker run or compose — shows only in the host figures. Memory figures are sampled, so they are lower bounds that can miss short spikes; memory pressure is the signal for those.
/// </summary>
public static class MetricsRecords
{
    public const int Version = 1;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new RoundedDouble() }
    };

    public static string Serialize(MetricsRecord record)
    {
        return JsonSerializer.Serialize(record, record.GetType(), Json);
    }

    /// <summary>One line of the stream as its record, or null for a kind this version doesn't know (a newer worker's) or a JSON object with no kind. Throws JsonException for a line that isn't JSON.</summary>
    public static MetricsRecord? Parse(string line)
    {
        using var document = JsonDocument.Parse(line);
        if (!document.RootElement.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return kind.GetString() switch
        {
            HostRecord.KindName => document.RootElement.Deserialize<HostRecord>(Json),
            ContainerRecord.KindName => document.RootElement.Deserialize<ContainerRecord>(Json),
            StepRecord.KindName => document.RootElement.Deserialize<StepRecord>(Json),
            PullRecord.KindName => document.RootElement.Deserialize<PullRecord>(Json),
            SampleRecord.KindName => document.RootElement.Deserialize<SampleRecord>(Json),
            StepSummaryRecord.KindName => document.RootElement.Deserialize<StepSummaryRecord>(Json),
            _ => null
        };
    }
}

public abstract record MetricsRecord
{
    public int V
    {
        get { return MetricsRecords.Version; }
    }

    public abstract string Kind { get; }

    public required DateTimeOffset Ts { get; init; }
}

/// <summary>The machine the job ran on. Repeated at the head of every chunk, since a buffered chunk can be lost in an outage and this one says how to read the rest.</summary>
public sealed record HostRecord : MetricsRecord
{
    public const string KindName = "host";

    public override string Kind
    {
        get { return KindName; }
    }

    public required string Hostname { get; init; }
    public required int CpuCount { get; init; }
    public long? MemTotalBytes { get; init; }
    public long? SwapTotalBytes { get; init; }

    /// <summary>The whole-disk devices the host disk figures sum over.</summary>
    public IReadOnlyList<string> Disks { get; init; } = [];

    public string? Kernel { get; init; }
}

/// <summary>A container seen for the first time: the samples name it only by id.</summary>
public sealed record ContainerRecord : MetricsRecord
{
    public const string KindName = "container";

    public override string Kind
    {
        get { return KindName; }
    }

    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? Image { get; init; }

    /// <summary>step (the step's own container), worker (the worker's, doing checkout and compile), or other (nested runs, services, anything else on the job network).</summary>
    public required string Role { get; init; }

    public DateTimeOffset? StartedAt { get; init; }
}

public sealed record StepRecord : MetricsRecord
{
    public const string KindName = "step";

    public override string Kind
    {
        get { return KindName; }
    }

    public required int Index { get; init; }
    public required string Name { get; init; }

    /// <summary>start or end.</summary>
    public required string Event { get; init; }

    /// <summary>On end: Success, Failed or Cancelled.</summary>
    public string? Status { get; init; }

    /// <summary>On start.</summary>
    public string? Image { get; init; }

    /// <summary>On start: the step runs an image an earlier step returned, so it depends on that step. Matched by reference, so a step that names the same tag itself counts too: a false dependency, never a missed one.</summary>
    public bool? ImageFromEarlierStep { get; init; }
}

/// <summary>
/// What a step used, written by the worker when the step ends, from every sample it took during it. CPU and IO are how far each container's running total rose over the step, which doesn't depend on when samples fell; peaks are sampled. The worker's own container is left out: it is doing the job's bookkeeping, not the step's work.
/// </summary>
public sealed record StepSummaryRecord : MetricsRecord
{
    public const string KindName = "stepSummary";

    public override string Kind
    {
        get { return KindName; }
    }

    public required int Index { get; init; }
    public required string Name { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
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
}

/// <summary>An image pull, marked so its cache-dependent time can be told apart from the step's own work.</summary>
public sealed record PullRecord : MetricsRecord
{
    public const string KindName = "pull";

    public override string Kind
    {
        get { return KindName; }
    }

    public required string Image { get; init; }

    /// <summary>start or end.</summary>
    public required string Event { get; init; }
}

public sealed record SampleRecord : MetricsRecord
{
    public const string KindName = "sample";

    public override string Kind
    {
        get { return KindName; }
    }

    public required double IntervalSeconds { get; init; }

    /// <summary>prepare (fetch, checkout, compile) or pipeline.</summary>
    public required string Phase { get; init; }

    public int? StepIndex { get; init; }
    public string? Step { get; init; }

    /// <summary>Other Ilmarinen job networks on this Docker host at the time: above zero, another job shared the machine and the host figures include its work.</summary>
    public int? Cotenants { get; init; }

    public HostFigures? Host { get; init; }
    public IReadOnlyList<ContainerFigures> Containers { get; init; } = [];

    /// <summary>Containers left out because the sample would otherwise not fit in one chunk.</summary>
    public int? ContainersOmitted { get; init; }
}

/// <summary>Whole-host figures. CPU is in cores busy over the interval; rates are null on the first sample, which has nothing to measure from.</summary>
public sealed record HostFigures
{
    public double? CpuUser { get; init; }
    public double? CpuSystem { get; init; }
    public double? CpuIowait { get; init; }
    public double? CpuSteal { get; init; }
    public long? ProcsRunning { get; init; }
    public long? ProcsBlocked { get; init; }
    public long? MemAvailableBytes { get; init; }
    public long? MemFreeBytes { get; init; }
    public long? MemDirtyBytes { get; init; }
    public long? SwapUsedBytes { get; init; }
    public double? DiskReadBytesPerSec { get; init; }
    public double? DiskWriteBytesPerSec { get; init; }
    public double? DiskReadOpsPerSec { get; init; }
    public double? DiskWriteOpsPerSec { get; init; }

    /// <summary>The busiest disk's fraction of the interval spent with IO in flight.</summary>
    public double? DiskBusy { get; init; }

    /// <summary>Pressure stall averages over the last 10 s, as percentages: the share of time some (or all) tasks waited on the resource.</summary>
    public double? PsiCpuSome { get; init; }
    public double? PsiMemSome { get; init; }
    public double? PsiMemFull { get; init; }
    public double? PsiIoSome { get; init; }
    public double? PsiIoFull { get; init; }
}

/// <summary>One container's figures. Rates cover the time since its previous sample, or since it started when this is its first.</summary>
public sealed record ContainerFigures
{
    public required string Id { get; init; }

    /// <summary>As on the container's record, repeated so a sample can be read without it.</summary>
    public required string Role { get; init; }

    public double? CpuCores { get; init; }
    public double CpuSecondsTotal { get; init; }

    /// <summary>Time the container was held back by a CPU limit, since it started.</summary>
    public double CpuThrottledSecondsTotal { get; init; }

    /// <summary>Working set: usage less inactive file cache.</summary>
    public long MemBytes { get; init; }

    public long? MemLimitBytes { get; init; }
    public long? FileDirtyBytes { get; init; }
    public double? ReadBytesPerSec { get; init; }
    public double? WriteBytesPerSec { get; init; }

    /// <summary>Since the container started; totals are what a step's summary is taken from, since rates depend on when samples happened to fall.</summary>
    public long ReadBytesTotal { get; init; }

    public long WriteBytesTotal { get; init; }
    public double? RxBytesPerSec { get; init; }
    public double? TxBytesPerSec { get; init; }
    public long? Pids { get; init; }
}

/// <summary>Writes doubles to four significant figures, or to a whole number when that keeps more: the figures are sampled estimates, and the full binary expansion would roughly double the stream.</summary>
internal sealed class RoundedDouble : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.GetDouble();
    }

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        // Not finite is a bug upstream, and the writer refuses it, which the sampler reports.
        if (value == 0 || !double.IsFinite(value))
        {
            writer.WriteNumberValue(value);
            return;
        }
        var digits = Math.Max(0, 3 - (int)Math.Floor(Math.Log10(Math.Abs(value))));
        writer.WriteNumberValue(Math.Round(value, Math.Min(digits, 15)));
    }
}
