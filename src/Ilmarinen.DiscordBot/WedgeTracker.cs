using System;

namespace Ilmarinen.DiscordBot;

public enum WedgeAnnouncement
{
    None,
    Wedged,
    Unwedged
}

/// <summary>
/// Turns successive polls for unsatisfiable jobs into the announcements that are due. A wedge is due once jobs have been unsatisfiable continuously for the grace period, and its recovery once none are. Each stays due until it is marked announced, so a message that failed to send goes out on a later poll instead of being lost.
/// </summary>
public class WedgeTracker
{
    private readonly TimeSpan _gracePeriod;
    private DateTime? _unsatisfiableSince;
    private bool _wedgeAnnounced;

    public WedgeTracker(TimeSpan gracePeriod)
    {
        _gracePeriod = gracePeriod;
    }

    public WedgeAnnouncement Observe(bool hasUnsatisfiableJobs, DateTime now)
    {
        if (!hasUnsatisfiableJobs)
        {
            _unsatisfiableSince = null;
            return _wedgeAnnounced ? WedgeAnnouncement.Unwedged : WedgeAnnouncement.None;
        }

        _unsatisfiableSince ??= now;
        return !_wedgeAnnounced && now - _unsatisfiableSince >= _gracePeriod ? WedgeAnnouncement.Wedged : WedgeAnnouncement.None;
    }

    /// <summary>
    /// A poll that failed says nothing about whether the jobs stayed stuck, so the grace period starts over from the next sighting — otherwise a sighting from just before a server outage would combine with one just after it into an alert. An announced wedge stays announced.
    /// </summary>
    public void ObservationFailed()
    {
        _unsatisfiableSince = null;
    }

    public void MarkWedgeAnnounced()
    {
        _wedgeAnnounced = true;
    }

    public void MarkRecoveryAnnounced()
    {
        _wedgeAnnounced = false;
    }
}
