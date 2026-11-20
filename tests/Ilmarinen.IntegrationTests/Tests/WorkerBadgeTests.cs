using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Server.Components;
using Ilmarinen.Server.Services;
using NUnit.Framework;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>A paused worker has to read as paused in the status column, whatever else it is doing, and not like one held back for some other reason.</summary>
[TestFixture]
public class WorkerBadgeTests
{
    private static WorkerView Worker(bool connected = true, bool ready = false, bool busy = false, bool paused = false, DiagnosticStatus? diagnostic = null, WorkerHold? hold = null)
    {
        return new WorkerView
        {
            Diagnostic = diagnostic is { } status ? new DiagnosticReport { Status = status, Summary = "", CheckedAt = DateTime.UtcNow, Steps = [] } : null,
            Id = Guid.NewGuid(),
            Name = "w",
            IsConnected = connected,
            IsReady = ready,
            IsPaused = paused,
            Hold = hold,
            CurrentJobs = busy ? [Guid.NewGuid()] : []
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
}
