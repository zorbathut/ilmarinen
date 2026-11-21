using Ilmarinen.Worker.Presence;
using NUnit.Framework;
using System;

namespace Ilmarinen.Core.Tests.Presence;

/// <summary>What the diagnostic says about the in-use watch: it passes only when the watch actually works, and says how to fix it when it doesn't.</summary>
[TestFixture]
public class PresenceReportTests
{
    private static readonly InputDevice Keyboard = new("event3", "AT Translated Set 2 keyboard");
    private static readonly InputDevice Mouse = new("event5", "Logitech USB Receiver");
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);
    private static readonly PresenceVerdict Away = new(false, "no input for 30 minutes");

    [Test]
    public void Watching_WithLogind_Passes()
    {
        var result = PresenceReport.Describe([Keyboard, Mouse], [Keyboard, Mouse], Away, null, Idle);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain(Keyboard.Name).And.Contain(Mouse.Name));
        Assert.That(result.Suggestion, Is.Null);
    }

    [Test]
    public void HostHasDevices_NoneReadable_FailsWithAFix()
    {
        var result = PresenceReport.Describe([Keyboard], [], null, null, Idle);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Suggestion, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void DevicesCantBeListed_NoneReadable_FailsWithAFix()
    {
        var result = PresenceReport.Describe(null, [], null, null, Idle);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Suggestion, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void HeadlessHost_Passes()
    {
        var result = PresenceReport.Describe([], [], null, null, Idle);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Is.Not.Empty);
    }

    [Test]
    public void Watching_WithoutLogind_FailsWithADifferentFix()
    {
        var noLogind = PresenceReport.Describe([Keyboard], [Keyboard], Away, new InvalidOperationException("no bus"), Idle);
        var noDevices = PresenceReport.Describe([Keyboard], [], null, null, Idle);

        Assert.That(noLogind.Success, Is.False);
        Assert.That(noLogind.Suggestion, Is.Not.Null.And.Not.Empty.And.Not.EqualTo(noDevices.Suggestion));
    }

    [Test]
    public void NotStarted_Fails()
    {
        var result = PresenceReport.Describe([Keyboard], null, null, null, Idle);

        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void Verdict_IsReported()
    {
        var away = PresenceReport.Describe([Keyboard], [Keyboard], Away, null, Idle);
        var here = PresenceReport.Describe([Keyboard], [Keyboard], new PresenceVerdict(true, "input 2 minutes ago"), null, Idle);

        Assert.That(here.Message, Is.Not.EqualTo(away.Message));
    }
}
