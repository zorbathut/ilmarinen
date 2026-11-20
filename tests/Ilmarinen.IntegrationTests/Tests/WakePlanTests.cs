using Ilmarinen.Protocol;
using Ilmarinen.Server.Services.Wake;
using NUnit.Framework;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>Which sleepers a round of waking wakes, how a wake ends, and who each waiting job is waiting on.</summary>
[TestFixture]
public class WakePlanTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-19T12:00:00Z");
    private static readonly WakeSettings Settings = new(Timeout: TimeSpan.FromMinutes(5), Backoff: TimeSpan.FromHours(1), QueuedDelay: TimeSpan.FromSeconds(30), DisconnectedDelay: TimeSpan.FromSeconds(90));

    private static WakeJob Job(WorkerPriority min = WorkerPriority.Low, TimeSpan? waited = null)
    {
        return new WakeJob(Guid.NewGuid(), min, Now - (waited ?? TimeSpan.FromMinutes(1)));
    }

    private static WakeWorker Sleeper(string name, WorkerPriority priority = WorkerPriority.Medium, string? mac = "aa:bb:cc:dd:ee:ff", bool paused = false, TimeSpan? asleepFor = null, bool hasRunningJob = false)
    {
        return new WakeWorker(Guid.NewGuid(), name, priority, paused, mac, Now - (asleepFor ?? TimeSpan.FromHours(1)), Connected: false, Ready: false, HasRunningJob: hasRunningJob);
    }

    private static WakeWorker Back(WakeWorker worker, bool ready = true, bool hasRunningJob = false)
    {
        return worker with { Connected = true, Ready = ready, HasRunningJob = hasRunningJob, LastSeen = Now };
    }

    private static WakeState Waking(params (WakeWorker Worker, DateTimeOffset Deadline)[] attempts)
    {
        return WakeState.Empty with { Attempts = attempts.ToDictionary(a => a.Worker.Id, a => new WakeAttempt(a.Worker.Id, a.Deadline - Settings.Timeout, a.Deadline)).ToImmutableDictionary() };
    }

    private static WakeRound Plan(IReadOnlyList<WakeJob> jobs, IReadOnlyList<WakeWorker> workers, WakeState? state = null, DateTimeOffset? at = null)
    {
        return WakePlan.Plan(jobs, workers, state ?? WakeState.Empty, Settings, at ?? Now);
    }

    [Test]
    public void AJobThatHasWaited_WakesOneSleeper_AndNoMore()
    {
        var a = Sleeper("a");
        var b = Sleeper("b");
        var job = Job();

        var round = Plan([job], [a, b]);

        Assert.That(round.Send, Has.Count.EqualTo(1));
        Assert.That(round.WaitingFor[job.Id], Is.EqualTo(round.Send.Single()));

        var next = Plan([job], [a, b], round.State, Now.AddSeconds(15));
        Assert.That(next.Send, Is.EqualTo(round.Send), "the same one again, its packet resent; not a second sleeper");
    }

    [Test]
    public void TwoJobs_WakeTwoSleepers()
    {
        var round = Plan([Job(), Job()], [Sleeper("a"), Sleeper("b"), Sleeper("c")]);

        Assert.That(round.Send, Has.Count.EqualTo(2));
    }

    [Test]
    public void AJobJustQueued_WakesNobodyYet()
    {
        var round = Plan([Job(waited: TimeSpan.FromSeconds(10))], [Sleeper("a")]);

        Assert.That(round.Send, Is.Empty);
    }

    [Test]
    public void NoSleeperThatCantTakeIt_IsWoken()
    {
        var round = Plan([Job(WorkerPriority.High)], [
            Sleeper("low", WorkerPriority.Low),
            Sleeper("no-address", WorkerPriority.High, mac: null),
            Sleeper("paused", WorkerPriority.High, paused: true),
            Sleeper("just-gone", WorkerPriority.High, asleepFor: TimeSpan.FromSeconds(30))]);

        Assert.That(round.Send, Is.Empty);
    }

    [Test]
    public void Sleepers_AreWokenHighestPriorityFirst_ThenMostRecentlySeen()
    {
        var medium = Sleeper("medium", WorkerPriority.Medium, asleepFor: TimeSpan.FromMinutes(5));
        var highLongAgo = Sleeper("high-long-ago", WorkerPriority.High, asleepFor: TimeSpan.FromDays(3));
        var highRecently = Sleeper("high-recently", WorkerPriority.High, asleepFor: TimeSpan.FromMinutes(10));

        var round = Plan([Job()], [medium, highLongAgo, highRecently]);

        Assert.That(round.Send, Is.EqualTo(new[] { highRecently.Id }));
    }

    [Test]
    public void AWokenWorkerThatComesBackReady_HasArrived()
    {
        var a = Sleeper("a");
        var state = Waking((a, Now.AddMinutes(3)));

        var round = Plan([Job()], [Back(a)], state);

        Assert.That(round.Ended, Is.EqualTo(new[] { (a.Id, WakeEnd.Arrived) }));
        Assert.That(round.State.Attempts, Is.Empty);
        Assert.That(round.Send, Is.Empty, "it is awake: the job is its to take");
    }

    [Test]
    public void AWokenWorkerThatComesBackBusy_HasArrived()
    {
        var a = Sleeper("a");

        var round = Plan([], [Back(a, ready: false, hasRunningJob: true)], Waking((a, Now.AddMinutes(3))));

        Assert.That(round.Ended, Is.EqualTo(new[] { (a.Id, WakeEnd.Arrived) }));
    }

    [Test]
    public void AWokenWorkerBackButNotReady_IsWaitedFor_ThenLetBeWithoutABackoff()
    {
        var a = Sleeper("a");
        var state = Waking((a, Now.AddMinutes(1)));

        var before = Plan([Job()], [Back(a, ready: false)], state);
        Assert.That(before.Ended, Is.Empty, "it may yet be ready: a diagnostic, a Resume");

        var after = Plan([Job()], [Back(a, ready: false)], state, Now.AddMinutes(2));
        Assert.That(after.Ended, Is.EqualTo(new[] { (a.Id, WakeEnd.AwakeButNotReady) }));
        Assert.That(after.State.FailedAt, Is.Empty);
    }

    [Test]
    public void AWakeThatTimesOut_LeavesTheWorkerAloneForTheBackoff_AndWakesTheNext()
    {
        var a = Sleeper("a", WorkerPriority.High);
        var b = Sleeper("b", WorkerPriority.Low);
        var job = Job();

        var round = Plan([job], [a, b], Waking((a, Now)));

        Assert.That(round.Ended, Is.EqualTo(new[] { (a.Id, WakeEnd.Unreachable) }));
        Assert.That(round.State.FailedAt[a.Id], Is.EqualTo(Now));
        Assert.That(round.Send, Is.EqualTo(new[] { b.Id }));
        Assert.That(round.WaitingFor[job.Id], Is.EqualTo(b.Id));
    }

    [Test]
    public void AFailedWorker_IsLeftAloneForTheBackoff_ThenTriedAgain_ButStaysFailedUntilSeen()
    {
        var a = Sleeper("a");
        var state = WakeState.Empty with { FailedAt = new Dictionary<Guid, DateTimeOffset> { [a.Id] = Now }.ToImmutableDictionary() };

        Assert.That(Plan([Job()], [a], state, Now + Settings.Backoff - TimeSpan.FromSeconds(1)).Send, Is.Empty);

        var round = Plan([Job()], [a], state, Now + Settings.Backoff);
        Assert.That(round.Send, Is.EqualTo(new[] { a.Id }));
        Assert.That(WakePlan.IsWakeable(a, round.State), Is.False, "not to be counted on until a wake works");
    }

    [Test]
    public void AWorkerThatComesBackByItself_IsForgivenItsFailure()
    {
        var a = Sleeper("a");
        var state = WakeState.Empty with { FailedAt = new Dictionary<Guid, DateTimeOffset> { [a.Id] = Now }.ToImmutableDictionary() };

        var round = Plan([], [Back(a)], state);

        Assert.That(round.State.FailedAt, Is.Empty);
    }

    [Test]
    public void AJobTakenByAnotherWorker_FreesItsWakeForTheNextJob()
    {
        var a = Sleeper("a");
        var b = Sleeper("b");
        var state = Waking((a, Now.AddMinutes(3)));
        var next = Job();

        var round = Plan([next], [a, b], state);

        Assert.That(round.WaitingFor[next.Id], Is.EqualTo(a.Id), "the wake in progress serves it; no second sleeper");
        Assert.That(round.Send, Is.EqualTo(new[] { a.Id }));

        var none = Plan([], [a, b], state);
        Assert.That(none.Send, Is.EqualTo(new[] { a.Id }), "with nothing left to wait on it, the machine is already waking: it carries on");
        Assert.That(none.WaitingFor, Is.Empty);
    }

    // Put to sleep with a job running, by someone closing the lid say: the job carries on when the machine next wakes, and waking it for that would fight the person who put it to sleep.
    [Test]
    public void ASleeperTheServerHasAJobRunningFor_IsLeftAsleep_WithNothingQueued()
    {
        var round = Plan([], [Sleeper("mid-job", hasRunningJob: true)]);

        Assert.That(round.Send, Is.Empty);
    }

    [Test]
    public void AWakeOfAWorkerWhoseAddressIsCleared_Stops_WithoutCountingAsAFailure()
    {
        var a = Sleeper("a");

        var round = Plan([Job()], [a with { Mac = null }], Waking((a, Now.AddMinutes(1))));

        Assert.That(round.State.Attempts, Is.Empty);
        Assert.That(round.Send, Is.Empty);
        Assert.That(round.State.FailedAt, Is.Empty);
    }

    [Test]
    public void AConnectedWorker_IsNeverWoken()
    {
        var round = Plan([Job()], [Back(Sleeper("a"), ready: false)]);

        Assert.That(round.Send, Is.Empty);
    }
}
