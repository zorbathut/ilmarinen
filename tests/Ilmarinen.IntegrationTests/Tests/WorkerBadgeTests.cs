using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Server.Components;
using Ilmarinen.Server.Services;
using NUnit.Framework;
using System.Linq;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>A paused worker has to read as paused in the status column, whatever else it is doing, and not like one held back for some other reason.</summary>
[TestFixture]
public class WorkerBadgeTests
{
    private static WorkerView Worker(bool connected = true, bool ready = false, bool busy = false, bool paused = false, DiagnosticStatus? diagnostic = null, WorkerHold? hold = null, bool waking = false, DateTimeOffset? wakeFailedAt = null, bool noRoom = false)
    {
        return new WorkerView
        {
            Diagnostic = diagnostic is { } status ? new DiagnosticReport { Status = status, Summary = "", CheckedAt = DateTime.UtcNow, Steps = [] } : null,
            Id = Guid.NewGuid(),
            Name = "w",
            Priority = new PriorityRef(Guid.NewGuid(), "Default"),
            IsConnected = connected,
            IsReady = ready,
            IsPaused = paused,
            Hold = hold,
            IsWaking = waking,
            WakeFailedAt = wakeFailedAt,
            CurrentJobs = busy ? [Guid.NewGuid()] : [],
            NoRoom = noRoom ? new NoRoomWorker(2, new DiskGap(30L << 30, 14L << 30), 300L << 30) : null
        };
    }

    [Test]
    public void APause_ChangesTheLabel_IdleBusyOrOffline()
    {
        Assert.Multiple(() =>
        {
            Assert.That(WorkerBadge.GetStatusLabel(Worker(paused: true)), Is.Not.EqualTo(WorkerBadge.GetStatusLabel(Worker())), "idle");
            Assert.That(WorkerBadge.GetStatusLabel(Worker(busy: true, paused: true)), Is.Not.EqualTo(WorkerBadge.GetStatusLabel(Worker(busy: true))), "busy");
            Assert.That(WorkerBadge.GetStatusLabel(Worker(connected: false, paused: true)), Is.Not.EqualTo(WorkerBadge.GetStatusLabel(Worker(connected: false))), "offline");
        });
    }

    [Test]
    public void APausedWorker_ReadsDifferentlyFromOneMerelyNotReady_AndFromAReadyOne()
    {
        var paused = WorkerBadge.GetStatusLabel(Worker(paused: true));

        Assert.That(paused, Is.Not.EqualTo(WorkerBadge.GetStatusLabel(Worker(ready: true))));
        Assert.That(paused, Is.Not.EqualTo(WorkerBadge.GetStatusLabel(Worker(busy: true, paused: true))));
    }

    [Test]
    public void APausedWorker_HasItsOwnBadge_UnlessOffline()
    {
        Assert.That(WorkerBadge.GetBadgeClass(Worker(paused: true)), Is.Not.EqualTo(WorkerBadge.GetBadgeClass(Worker())));
        Assert.That(WorkerBadge.GetBadgeClass(Worker(connected: false, paused: true)), Is.EqualTo(WorkerBadge.GetBadgeClass(Worker(connected: false))), "offline is what matters most");
    }

    [Test]
    public void ADegradedPausedWorker_ReadsBothWays()
    {
        var both = WorkerBadge.GetStatusLabel(Worker(paused: true, diagnostic: DiagnosticStatus.Degraded));

        Assert.That(both, Is.Not.EqualTo(WorkerBadge.GetStatusLabel(Worker(paused: true))));
        Assert.That(both, Is.Not.EqualTo(WorkerBadge.GetStatusLabel(Worker(diagnostic: DiagnosticStatus.Degraded))));
    }

    [Test]
    public void AHeldWorker_SaysWhy_IdleOrBusy()
    {
        var held = WorkerBadge.GetStatusLabel(Worker(hold: WorkerHold.ShuttingDown));
        var heldBusy = WorkerBadge.GetStatusLabel(Worker(busy: true, hold: WorkerHold.ShuttingDown));

        Assert.That(held, Is.Not.EqualTo(WorkerBadge.GetStatusLabel(Worker())), "not just \"not ready\"");
        Assert.That(heldBusy, Is.Not.EqualTo(WorkerBadge.GetStatusLabel(Worker(busy: true))));
        Assert.That(heldBusy, Is.Not.EqualTo(held));
    }

    [Test]
    public void EachHold_ReadsDifferently()
    {
        var labels = Enum.GetValues<WorkerHold>().Select(h => WorkerBadge.GetStatusLabel(Worker(hold: h))).ToList();

        Assert.That(labels.Distinct().Count(), Is.EqualTo(labels.Count));
        Assert.That(labels, Does.Not.Contain(WorkerBadge.GetStatusLabel(Worker())));
    }

    [Test]
    public void AnOfflineWorker_SaysWhetherItIsBeingWoken_OrDidntWake()
    {
        var offline = WorkerBadge.GetStatusLabel(Worker(connected: false));
        var waking = WorkerBadge.GetStatusLabel(Worker(connected: false, waking: true));
        var unreachable = WorkerBadge.GetStatusLabel(Worker(connected: false, wakeFailedAt: DateTimeOffset.UtcNow));

        Assert.That(new[] { offline, waking, unreachable }.Distinct().Count(), Is.EqualTo(3));
        Assert.That(WorkerBadge.GetBadgeClass(Worker(connected: false, waking: true)), Is.Not.EqualTo(WorkerBadge.GetBadgeClass(Worker(connected: false))));
    }

    [Test]
    public void AReadyWorkerWithNoRoom_ReadsBlocked_WithItsOwnLight_AndSaysWhy()
    {
        var blocked = Worker(ready: true, noRoom: true);

        Assert.That(WorkerBadge.GetBadgeClass(blocked), Is.Not.EqualTo(WorkerBadge.GetBadgeClass(Worker(ready: true))));
        Assert.That(WorkerBadge.GetStatusLabel(blocked), Is.Not.EqualTo(WorkerBadge.GetStatusLabel(Worker(ready: true))));
        Assert.That(WorkerBadge.GetStatusDetail(blocked), Is.Not.Null.And.Not.Empty);
        Assert.That(WorkerBadge.GetStatusDetail(Worker(ready: true)), Is.Null, "nothing to fix");
    }

    [Test]
    public void ADegradedBlockedWorker_ReadsBothWays()
    {
        var both = WorkerBadge.GetStatusLabel(Worker(ready: true, noRoom: true, diagnostic: DiagnosticStatus.Degraded));

        Assert.That(both, Is.Not.EqualTo(WorkerBadge.GetStatusLabel(Worker(ready: true, noRoom: true))));
        Assert.That(both, Is.Not.EqualTo(WorkerBadge.GetStatusLabel(Worker(ready: true, diagnostic: DiagnosticStatus.Degraded))));
    }

    [Test]
    public void AnOfflineWorkerWithNoRoom_KeepsTheOfflineLight_ButSaysSo()
    {
        var blocked = Worker(connected: false, noRoom: true);

        Assert.That(WorkerBadge.GetBadgeClass(blocked), Is.EqualTo(WorkerBadge.GetBadgeClass(Worker(connected: false))));
        Assert.That(WorkerBadge.GetStatusLabel(blocked), Is.Not.EqualTo(WorkerBadge.GetStatusLabel(Worker(connected: false))));
        Assert.That(WorkerBadge.GetStatusDetail(blocked), Is.Not.Null.And.Not.Empty);
        Assert.That(WorkerBadge.GetBadgeClass(Worker(connected: false, waking: true, noRoom: true)), Is.EqualTo(WorkerBadge.GetBadgeClass(Worker(connected: false, waking: true))), "being woken still shows");
    }
}
