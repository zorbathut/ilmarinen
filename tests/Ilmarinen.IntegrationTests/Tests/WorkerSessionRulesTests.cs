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
        Assert.That(WorkerReadiness.IsReady(Session(Idle(DiagnosticStatus.Healthy)), diagnosticOwed: false, hasRunningJob: false, serverBundleHash: null), Is.True);
    }

    [Test]
    public void IsReady_FalseForEachThingThatHoldsAWorkerBack()
    {
        var ready = Idle(DiagnosticStatus.Healthy);
        Assert.Multiple(() =>
        {
            Assert.That(WorkerReadiness.IsReady(Session(ready, resumed: false), false, false, null), Is.False, "not resumed");
            Assert.That(WorkerReadiness.IsReady(Session(null), false, false, null), Is.False, "no state");
            Assert.That(WorkerReadiness.IsReady(Session(ready with { Activity = WorkerActivity.RunningJob }), false, false, null), Is.False, "running a job");
            Assert.That(WorkerReadiness.IsReady(Session(ready with { Activity = WorkerActivity.RunningDiagnostic }), false, false, null), Is.False, "running a diagnostic");
            Assert.That(WorkerReadiness.IsReady(Session(Idle(DiagnosticStatus.Unhealthy)), false, false, null), Is.False, "unhealthy");
            Assert.That(WorkerReadiness.IsReady(Session(Idle(null)), false, false, null), Is.False, "no verdict yet");
            Assert.That(WorkerReadiness.IsReady(Session(ready), diagnosticOwed: true, false, null), Is.False, "a diagnostic owed");
            Assert.That(WorkerReadiness.IsReady(Session(ready), false, hasRunningJob: true, null), Is.False, "a job Running for it");
            Assert.That(WorkerReadiness.IsReady(Session(ready, bundleHash: "OLD"), false, false, serverBundleHash: "NEW"), Is.False, "a stale bundle");
        });
    }

    [Test]
    public void IsReady_DegradedOrClassicWorker_StillReady()
    {
        Assert.That(WorkerReadiness.IsReady(Session(Idle(DiagnosticStatus.Degraded)), false, false, null), Is.True);
        Assert.That(WorkerReadiness.IsReady(Session(Idle(DiagnosticStatus.Healthy)), false, false, serverBundleHash: "NEW"), Is.True, "a classic worker reports no bundle and is never stale");
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
