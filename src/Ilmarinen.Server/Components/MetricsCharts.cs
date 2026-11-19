using Ilmarinen.Protocol.Metrics;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System;

namespace Ilmarinen.Server.Components;

/// <summary>A line of a chart: its name for the legend, a CSS class for its colour, and its points in SVG coordinates.</summary>
public sealed record ChartLine(string Name, string CssClass, string Points);

/// <summary>A step's start, at an x position.</summary>
public sealed record ChartMarker(double X, string Label);

/// <summary>A stretch of the job where another job shared the host, so the host's figures include its work.</summary>
public sealed record ChartBand(double X1, double X2);

public sealed record Chart(string Title, string Unit, double YMax, IReadOnlyList<ChartLine> Lines, IReadOnlyList<ChartMarker> Markers, IReadOnlyList<ChartBand> SharedHost);

/// <summary>
/// The job page's resource charts, drawn from a job's metrics records: CPU, memory and disk over the job's time, with each step's start marked. Everything here is layout; the page only renders it.
/// </summary>
public static class MetricsCharts
{
    public const double Width = 600;
    public const double Height = 120;

    /// <summary>At most this many points a line: one per pixel column, each the highest value in its stretch, so a spike survives.</summary>
    public const int MaxPoints = 600;

    private const double GiB = 1024.0 * 1024 * 1024;
    private const double MiB = 1024.0 * 1024;

    public static IReadOnlyList<Chart> Build(IReadOnlyList<MetricsRecord> records)
    {
        var samples = records.OfType<SampleRecord>().OrderBy(s => s.Ts).ToList();
        if (samples.Count == 0)
        {
            return [];
        }

        var start = samples[0].Ts;
        var end = samples[^1].Ts;
        var span = Math.Max((end - start).TotalSeconds, 1);
        var memTotal = records.OfType<HostRecord>().Select(h => h.MemTotalBytes).FirstOrDefault(m => m != null);

        double X(DateTimeOffset at)
        {
            return Math.Clamp((at - start).TotalSeconds / span, 0, 1) * Width;
        }

        var markers = records.OfType<StepRecord>()
            .Where(s => s.Event == "start")
            .GroupBy(s => s.Index)
            .Select(g => g.First())
            .OrderBy(s => s.Ts)
            .Select(s => new ChartMarker(X(s.Ts), s.Name))
            .ToList();

        var bands = SharedHostBands(samples, X);

        IEnumerable<ContainerFigures> JobContainers(SampleRecord sample)
        {
            return sample.Containers.Where(c => c.Role != "worker");
        }

        Chart Make(string title, string unit, double scale, params (string Name, string Css, Func<SampleRecord, double?> Value)[] series)
        {
            var points = series.Select(s => Downsample(samples.Select(sample => (X(sample.Ts), s.Value(sample) / scale)).ToList())).ToList();
            var yMax = points.SelectMany(p => p).Select(p => p.Y).DefaultIfEmpty(0).Max();
            var top = yMax > 0 ? yMax : 1;
            var lines = series.Zip(points, (s, p) => new ChartLine(s.Name, s.Css, Polyline(p, top))).ToList();
            return new Chart(title, unit, yMax, lines, markers, bands);
        }

        return
        [
            Make("CPU", "cores", 1,
                ("Host", "metric-host", s => s.Host is { CpuUser: { } user, CpuSystem: { } system } ? user + system : null),
                ("Job containers", "metric-job", s => JobContainers(s).Sum(c => c.CpuCores ?? 0))),
            Make("Memory", "GiB", GiB,
                ("Host used", "metric-host", s => memTotal is { } total && s.Host?.MemAvailableBytes is { } available ? total - available : null),
                ("Job containers", "metric-job", s => JobContainers(s).Sum(c => c.MemBytes))),
            Make("Disk", "MiB/s", MiB,
                ("Read", "metric-read", s => s.Host?.DiskReadBytesPerSec),
                ("Write", "metric-write", s => s.Host?.DiskWriteBytesPerSec)),
        ];
    }

    /// <summary>Points thinned to <see cref="MaxPoints"/>, each keeping the highest value of the points it stands for. A missing value leaves its point out.</summary>
    public static IReadOnlyList<(double X, double Y)> Downsample(IReadOnlyList<(double X, double? Y)> points)
    {
        var present = points.Where(p => p.Y != null).Select(p => (p.X, Y: p.Y!.Value)).ToList();
        if (present.Count <= MaxPoints)
        {
            return present;
        }

        return present
            .GroupBy(p => Math.Min(MaxPoints - 1, (int)(p.X / Width * MaxPoints)))
            .OrderBy(g => g.Key)
            .Select(g => (g.First().X, g.Max(p => p.Y)))
            .ToList();
    }

    /// <summary>An SVG polyline's points attribute, with y = 0 at the bottom and <paramref name="yMax"/> at the top.</summary>
    public static string Polyline(IReadOnlyList<(double X, double Y)> points, double yMax)
    {
        return string.Join(" ", points.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.X:0.#},{Height - p.Y / yMax * Height:0.#}")));
    }

    private static IReadOnlyList<ChartBand> SharedHostBands(IReadOnlyList<SampleRecord> samples, Func<DateTimeOffset, double> x)
    {
        var bands = new List<ChartBand>();
        double? from = null;
        for (var i = 0; i < samples.Count; i++)
        {
            var shared = samples[i].Cotenants > 0;
            if (shared && from == null)
            {
                from = x(samples[i].Ts);
            }
            if (!shared && from != null)
            {
                bands.Add(new ChartBand(from.Value, x(samples[i].Ts)));
                from = null;
            }
        }
        if (from != null)
        {
            bands.Add(new ChartBand(from.Value, Width));
        }
        return bands;
    }
}
