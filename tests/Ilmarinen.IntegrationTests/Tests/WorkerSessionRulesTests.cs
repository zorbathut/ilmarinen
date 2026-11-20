using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using NUnit.Framework;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>The two rules the server decides a worker's session by: which handshake supersedes which, and when a session can be handed a job.</summary>
[TestFixture]
public class WorkerSessionRulesTests
{
    private static readonly Guid Worker = Guid.NewGuid();
    private static readonly Guid InstanceA = Guid.NewGuid();
    private static readonly Guid InstanceB = Guid.NewGuid();

    [Test]
    public void Supersedes_WithNoCurrentSession_Accepts()
    {
        Assert.That(SessionEpoch.Supersedes(null, [], InstanceA, 1), Is.True);
    }

    [TestCase(2, true)]
    [TestCase(1, false)]
    [TestCase(0, false)]
    public void Supersedes_SameInstance_OnlyALaterConnection(long counter, bool supersedes)
    {
        var current = new WorkerSession(Worker, "conn", InstanceA, 1);

        Assert.That(SessionEpoch.Supersedes(current, [], InstanceA, counter), Is.EqualTo(supersedes));
    }

    [Test]
    public void Supersedes_ADifferentInstance_WhateverItsCounter()
    {
        var current = new WorkerSession(Worker, "conn", InstanceA, 50);

        Assert.That(SessionEpoch.Supersedes(current, [], InstanceB, 1), Is.True, "a restarted process counts from 1 again");
    }

    [Test]
    public void Supersedes_ARetiredInstance_Never()
    {
        var current = new WorkerSession(Worker, "conn", InstanceB, 1);

        Assert.That(SessionEpoch.Supersedes(current, [InstanceA], InstanceA, 99), Is.False);
        Assert.That(SessionEpoch.Supersedes(null, [InstanceA], InstanceA, 99), Is.False, "not even with nobody holding the session");
    }

    [Test]
    public void IsReady_ResumedIdleAndHealthy()
    {
        Assert.That(Ready(Session(Idle(DiagnosticStatus.Healthy))), Is.True);
    }

    [Test]
    public void IsReady_FalseForEachThingThatHoldsAWorkerBack()
    {
        var ready = Idle(DiagnosticStatus.Healthy);
        Assert.Multiple(() =>
        {
            Assert.That(Ready(Session(ready, resumed: false)), Is.False, "not resumed");
            Assert.That(Ready(Session(null)), Is.False, "no state");
            Assert.That(Ready(Session(ready with { Activity = WorkerActivity.RunningJob })), Is.False, "running a job");
            Assert.That(Ready(Session(ready with { Activity = WorkerActivity.RunningDiagnostic })), Is.False, "running a diagnostic");
            Assert.That(Ready(Session(Idle(DiagnosticStatus.Unhealthy))), Is.False, "unhealthy");
            Assert.That(Ready(Session(Idle(null))), Is.False, "no verdict yet");
            Assert.That(Ready(Session(ready), diagnosticOwed: true), Is.False, "a diagnostic owed");
            Assert.That(Ready(Session(ready), paused: true), Is.False, "paused by an operator");
            Assert.That(Ready(Session(ready), hasRunningJob: true), Is.False, "a job Running for it");
            Assert.That(Ready(Session(ready, bundleHash: "OLD"), serverBundleHash: "NEW"), Is.False, "a stale bundle");
            Assert.That(Ready(Session(ready with { Hold = WorkerHold.InUse })), Is.False, "a hold");
        });
    }

    [Test]
    public void IsReady_DegradedOrClassicWorker_StillReady()
    {
        Assert.That(Ready(Session(Idle(DiagnosticStatus.Degraded))), Is.True);
        Assert.That(Ready(Session(Idle(DiagnosticStatus.Healthy)), serverBundleHash: "NEW"), Is.True, "a classic worker reports no bundle and is never stale");
    }

    private static bool Ready(WorkerSession session, bool diagnosticOwed = false, bool paused = false, bool hasRunningJob = false, string? serverBundleHash = null)
    {
        return WorkerReadiness.IsReady(session, session.State?.Hold, diagnosticOwed: diagnosticOwed, paused: paused, hasRunningJob: hasRunningJob, serverBundleHash: serverBundleHash);
    }

    private static WorkerState Idle(DiagnosticStatus? diagnostic)
    {
        return new WorkerState { Seq = 1, Activity = WorkerActivity.Idle, DiagnosticStatus = diagnostic };
    }

    private static WorkerSession Session(WorkerState? state, bool resumed = true, string? bundleHash = null)
    {
        return new WorkerSession(Worker, "conn", InstanceA, 1) { State = state, Resumed = resumed, BundleHash = bundleHash };
    }
}
