using Cronos;
using System;

namespace Ilmarinen.Server.Services;

/// <summary>
/// Converts cron expressions between UTC (storage) and a local timezone (display/input).
/// Works by computing the next occurrence in UTC and mapping it to the target timezone
/// to derive shifted cron fields.
/// </summary>
public static class CronTimezoneHelper
{
    /// <summary>
    /// Converts a UTC cron expression to a local-time cron expression for display.
    /// Returns null if the expression is invalid.
    /// </summary>
    public static string? UtcToLocal(string utcCron, TimeZoneInfo tz)
    {
        if (!CronValidator.TryParse(utcCron, out var expr) || expr == null)
            return null;

        // Get next occurrence in UTC, then convert to local to see the offset
        var baseUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var nextUtc = expr.GetNextOccurrence(baseUtc, inclusive: true);
        if (nextUtc == null)
            return null;

        var nextLocal = TimeZoneInfo.ConvertTimeFromUtc(nextUtc.Value, tz);
        var offset = nextLocal - nextUtc.Value;

        return ShiftCron(utcCron, offset);
    }

    /// <summary>
    /// Converts a local-time cron expression to UTC for storage.
    /// Returns null if the expression is invalid.
    /// </summary>
    public static string? LocalToUtc(string localCron, TimeZoneInfo tz)
    {
        if (!CronValidator.TryParse(localCron, out var expr) || expr == null)
            return null;

        // Get next occurrence treating the cron as local time, then find the UTC offset.
        // Cronos requires DateTimeKind.Utc, so we use a local-time value but tag it as Utc
        // to satisfy the API — we're only using it to figure out when the cron would fire
        // in the user's local clock, not as a real UTC instant.
        var baseUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var baseLocal = TimeZoneInfo.ConvertTimeFromUtc(baseUtc, tz);
        var baseLocalAsUtc = DateTime.SpecifyKind(baseLocal, DateTimeKind.Utc);
        var nextLocal = expr.GetNextOccurrence(baseLocalAsUtc, inclusive: true);
        if (nextLocal == null)
            return null;

        var nextLocalAsUtc = DateTime.SpecifyKind(nextLocal.Value, DateTimeKind.Unspecified);
        var nextUtc = TimeZoneInfo.ConvertTimeToUtc(nextLocalAsUtc, tz);
        var offset = nextUtc - nextLocal.Value;

        return ShiftCron(localCron, offset);
    }

    /// <summary>
    /// Returns a human-readable description of the next occurrence in local time.
    /// </summary>
    public static string? NextOccurrenceLocal(string utcCron, TimeZoneInfo tz)
    {
        if (!CronValidator.TryParse(utcCron, out var expr) || expr == null)
            return null;

        var nextUtc = expr.GetNextOccurrence(DateTime.UtcNow, inclusive: false);
        if (nextUtc == null)
            return null;

        var nextLocal = TimeZoneInfo.ConvertTimeFromUtc(nextUtc.Value, tz);
        return nextLocal.ToString("yyyy-MM-dd HH:mm");
    }

    private static string ShiftCron(string cron, TimeSpan offset)
    {
        var parts = cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5)
            return cron;

        var minutePart = parts[0];
        var hourPart = parts[1];

        // Only shift simple numeric minute/hour fields.
        // Complex expressions (*/2, 1-5, 1,3,5) are returned as-is —
        // the "next run" display still shows the correct local time.
        if (!int.TryParse(minutePart, out var minute) || !int.TryParse(hourPart, out var hour))
            return cron;

        var totalMinutes = hour * 60 + minute + (int)offset.TotalMinutes;

        // Wrap around midnight
        while (totalMinutes < 0) totalMinutes += 24 * 60;
        totalMinutes %= 24 * 60;

        var newHour = totalMinutes / 60;
        var newMinute = totalMinutes % 60;

        var dayShift = (int)Math.Round(offset.TotalMinutes + (hour * 60 + minute) - (newHour * 60 + newMinute)) / (24 * 60);

        parts[0] = newMinute.ToString();
        parts[1] = newHour.ToString();

        // Shift day-of-week if the hour shift crosses midnight
        if (dayShift != 0 && parts[4] != "*")
        {
            if (TryShiftDayOfWeek(parts[4], dayShift, out var shifted))
                parts[4] = shifted;
        }

        return string.Join(' ', parts);
    }

    private static bool TryShiftDayOfWeek(string dow, int dayShift, out string result)
    {
        result = dow;

        // Handle simple numeric day: 0-6
        if (int.TryParse(dow, out var day))
        {
            result = (((day + dayShift) % 7) + 7) % 7 + "";
            return true;
        }

        // Handle comma-separated: 1,3,5
        if (dow.Contains(','))
        {
            var days = dow.Split(',');
            var shifted = new string[days.Length];
            for (int i = 0; i < days.Length; i++)
            {
                if (!int.TryParse(days[i], out var d))
                    return false;
                shifted[i] = ((d + dayShift) % 7 + 7) % 7 + "";
            }
            result = string.Join(',', shifted);
            return true;
        }

        // Handle range: 1-5
        if (dow.Contains('-'))
        {
            var range = dow.Split('-');
            if (range.Length == 2 && int.TryParse(range[0], out var start) && int.TryParse(range[1], out var end))
            {
                result = $"{((start + dayShift) % 7 + 7) % 7}-{((end + dayShift) % 7 + 7) % 7}";
                return true;
            }
        }

        return false;
    }
}
