using Ilmarinen.DiscordBot;
using NUnit.Framework;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// When the Discord bot announces that queued jobs are stuck, and when it announces that they no longer are.
/// </summary>
[TestFixture]
public class WedgeTrackerTests
{
    private static readonly TimeSpan GracePeriod = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A tracker that has announced a wedge, first seen at Start and announced at the end of the grace period.
    /// </summary>
    private static WedgeTracker AnnouncedWedge()
    {
        var tracker = new WedgeTracker(GracePeriod);
        tracker.Observe(true, Start);
        Assert.That(tracker.Observe(true, Start + GracePeriod), Is.EqualTo(WedgeAnnouncement.Wedged));
        tracker.MarkWedgeAnnounced();
        return tracker;
    }

    [Test]
    public void Observe_StuckForLessThanTheGracePeriod_ReportsNothing()
    {
        var tracker = new WedgeTracker(GracePeriod);

        Assert.That(tracker.Observe(true, Start), Is.EqualTo(WedgeAnnouncement.None));
        Assert.That(tracker.Observe(true, Start + GracePeriod - TimeSpan.FromSeconds(1)), Is.EqualTo(WedgeAnnouncement.None));
    }

    [Test]
    public void Observe_StuckForTheGracePeriod_ReportsAWedgeUntilItIsAnnounced()
    {
        var tracker = new WedgeTracker(GracePeriod);
        tracker.Observe(true, Start);

        Assert.That(tracker.Observe(true, Start + GracePeriod), Is.EqualTo(WedgeAnnouncement.Wedged));
        Assert.That(tracker.Observe(true, Start + GracePeriod + PollInterval), Is.EqualTo(WedgeAnnouncement.Wedged),
            "a wedge whose message never went out must be reported again");

        tracker.MarkWedgeAnnounced();

        Assert.That(tracker.Observe(true, Start + GracePeriod + 2 * PollInterval), Is.EqualTo(WedgeAnnouncement.None),
            "an announced wedge that is still going must not be announced twice");
    }

    [Test]
    public void Observe_AnnouncedWedgeClears_ReportsARecoveryUntilItIsAnnounced()
    {
        var tracker = AnnouncedWedge();
        var cleared = Start + GracePeriod + PollInterval;

        Assert.That(tracker.Observe(false, cleared), Is.EqualTo(WedgeAnnouncement.Unwedged));
        Assert.That(tracker.Observe(false, cleared + PollInterval), Is.EqualTo(WedgeAnnouncement.Unwedged),
            "a recovery whose message never went out must be reported again");

        tracker.MarkRecoveryAnnounced();

        Assert.That(tracker.Observe(false, cleared + 2 * PollInterval), Is.EqualTo(WedgeAnnouncement.None));
    }

    [Test]
    public void Observe_StuckAgainAfterAnAnnouncedRecovery_WaitsAFullGracePeriod()
    {
        var tracker = AnnouncedWedge();
        var cleared = Start + GracePeriod + PollInterval;
        Assert.That(tracker.Observe(false, cleared), Is.EqualTo(WedgeAnnouncement.Unwedged));
        tracker.MarkRecoveryAnnounced();

        var stuckAgainAt = cleared + PollInterval;
        Assert.That(tracker.Observe(true, stuckAgainAt), Is.EqualTo(WedgeAnnouncement.None));
        Assert.That(tracker.Observe(true, stuckAgainAt + GracePeriod - PollInterval), Is.EqualTo(WedgeAnnouncement.None));
        Assert.That(tracker.Observe(true, stuckAgainAt + GracePeriod), Is.EqualTo(WedgeAnnouncement.Wedged));
    }

    [Test]
    public void Observe_ClearsBeforeTheGracePeriod_RestartsTheGracePeriod()
    {
        var tracker = new WedgeTracker(GracePeriod);
        var clearedAt = Start + TimeSpan.FromMinutes(2);
        var stuckAgainAt = Start + TimeSpan.FromMinutes(3);

        tracker.Observe(true, Start);
        Assert.That(tracker.Observe(false, clearedAt), Is.EqualTo(WedgeAnnouncement.None));
        tracker.Observe(true, stuckAgainAt);

        Assert.That(tracker.Observe(true, Start + GracePeriod), Is.EqualTo(WedgeAnnouncement.None),
            "the grace period counts from when jobs got stuck again, not from the first sighting");
        Assert.That(tracker.Observe(true, stuckAgainAt + GracePeriod), Is.EqualTo(WedgeAnnouncement.Wedged));
    }

    [Test]
    public void Observe_UnannouncedWedgeClears_ReportsNothing()
    {
        var tracker = new WedgeTracker(GracePeriod);
        tracker.Observe(true, Start);
        Assert.That(tracker.Observe(true, Start + GracePeriod), Is.EqualTo(WedgeAnnouncement.Wedged));

        Assert.That(tracker.Observe(false, Start + GracePeriod + PollInterval), Is.EqualTo(WedgeAnnouncement.None),
            "nothing was announced, so there is nothing to recover from");
    }

    [Test]
    public void ObservationFailed_DuringTheGracePeriod_RestartsIt()
    {
        var tracker = new WedgeTracker(GracePeriod);
        tracker.Observe(true, Start);

        tracker.ObservationFailed();
        var seenAgainAt = Start + GracePeriod;

        Assert.That(tracker.Observe(true, seenAgainAt), Is.EqualTo(WedgeAnnouncement.None),
            "a sighting from before an outage must not combine with one after it into a wedge");
        Assert.That(tracker.Observe(true, seenAgainAt + GracePeriod), Is.EqualTo(WedgeAnnouncement.Wedged));
    }

    [Test]
    public void ObservationFailed_AfterAnAnnouncedWedge_KeepsItAnnounced()
    {
        var tracker = AnnouncedWedge();

        tracker.ObservationFailed();

        Assert.That(tracker.Observe(true, Start + 2 * GracePeriod), Is.EqualTo(WedgeAnnouncement.None));
        Assert.That(tracker.Observe(false, Start + 2 * GracePeriod + PollInterval), Is.EqualTo(WedgeAnnouncement.Unwedged));
    }

    [Test]
    public void Observe_StuckAgainBeforeTheRecoveryIsAnnounced_IsStillTheSameWedge()
    {
        var tracker = AnnouncedWedge();
        var cleared = Start + GracePeriod + PollInterval;

        Assert.That(tracker.Observe(false, cleared), Is.EqualTo(WedgeAnnouncement.Unwedged));
        Assert.That(tracker.Observe(true, cleared + PollInterval), Is.EqualTo(WedgeAnnouncement.None));
        Assert.That(tracker.Observe(true, cleared + PollInterval + GracePeriod), Is.EqualTo(WedgeAnnouncement.None),
            "the recovery never went out, so the wedge that was announced is still the current one");
        Assert.That(tracker.Observe(false, cleared + 2 * PollInterval + GracePeriod), Is.EqualTo(WedgeAnnouncement.Unwedged));
    }
}
