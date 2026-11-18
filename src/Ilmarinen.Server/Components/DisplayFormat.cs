using Ilmarinen.Protocol.Responses;
using System.Globalization;
using System;

namespace Ilmarinen.Server.Components;

public static class DisplayFormat
{
    public static string Size(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:F1} GB", bytes / (1024.0 * 1024.0 * 1024.0));
        }
        if (bytes >= 1024 * 1024)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:F1} MB", bytes / (1024.0 * 1024.0));
        }
        if (bytes >= 1024)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:F1} KB", bytes / 1024.0);
        }
        return $"{bytes} B";
    }

    public static string Duration(TimeSpan duration)
    {
        if (duration.TotalMinutes < 1)
        {
            return $"{duration.Seconds}s";
        }
        if (duration.TotalHours < 1)
        {
            return $"{duration.Minutes}m {duration.Seconds}s";
        }
        return $"{(int)duration.TotalHours}h {duration.Minutes}m";
    }

    /// <summary>
    /// How long a job has run: up to <paramref name="now"/> while it's running, up to its completion once it's done.
    /// </summary>
    public static string JobElapsed(JobInfo job, DateTime now)
    {
        if (job.StartedAt == null)
        {
            return "---";
        }
        return Duration((job.CompletedAt ?? now) - job.StartedAt.Value);
    }

    /// <summary>
    /// A job's elapsed time, followed by how long it's expected to take while it's still running and there's an estimate for it.
    /// </summary>
    public static string JobProgress(JobInfo job, TimeSpan? estimate, DateTime now)
    {
        var elapsed = JobElapsed(job, now);
        if (estimate == null || job.CompletedAt != null)
        {
            return elapsed;
        }
        return $"{elapsed} / ~{Duration(estimate.Value)}";
    }

    /// <summary>
    /// A size that is measured in the background: <paramref name="bytes"/> is null both before the measurement arrives and when it failed, and the two should read differently.
    /// </summary>
    public static string MeasuredSize(bool measured, long? bytes)
    {
        if (!measured)
        {
            return "measuring…";
        }
        if (bytes == null)
        {
            return "unknown";
        }
        return Size(bytes.Value);
    }

    /// <summary>
    /// How long before <paramref name="now"/> something happened, coarsely. A time in the future (the worker's clock ahead of the server's) reads as now.
    /// </summary>
    public static string Ago(DateTimeOffset when, DateTimeOffset now)
    {
        var age = now - when;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }
        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes}m ago";
        }
        if (age < TimeSpan.FromDays(1))
        {
            return $"{(int)age.TotalHours}h ago";
        }
        return $"{(int)age.TotalDays}d ago";
    }
}
