using System;

namespace Ilmarinen.Server.Components;

public static class ShortId
{
    /// <summary>
    /// The last 8 hex digits of an ID, for display. Our IDs are UUIDv7, whose leading digits are a millisecond timestamp — the first 8 only change every ~65 seconds, so a prefix can't tell apart anything created close together. The tail is random.
    /// </summary>
    public static string From(Guid id)
    {
        return id.ToString("N")[^8..];
    }
}
