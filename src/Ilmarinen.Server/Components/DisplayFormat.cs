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
}
