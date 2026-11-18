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
