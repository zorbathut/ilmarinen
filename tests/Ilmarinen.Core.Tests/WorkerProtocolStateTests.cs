using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Ilmarinen.Worker.Services;
using NUnit.Framework;
using System.Linq;
using System;

namespace Ilmarinen.Core.Tests;

/// <summary>The worker's half of the protocol: what it does with each assignment, job end and diagnostic request, and what it then tells the server.</summary>
[TestFixture]
public class WorkerProtocolStateTests
{
    [Test]
    public void Assign_WhenIdle_TakesTheJobAndReportsIt()
    {
        var state = new WorkerProtocolState();
        var before = state.Snapshot().Seq;
        var job = Assignment();

        Assert.That(state.Assign(job), Is.Null, "taken, not handed back");

        var after = state.Snapshot();
        Assert.That(after.Activity, Is.EqualTo(WorkerActivity.RunningJob));
        Assert.That(after.JobId, Is.EqualTo(job.Id));
        Assert.That(after.AssignmentId, Is.EqualTo(job.AssignmentId));
        Assert.That(after.Seq, Is.GreaterThan(before));
    }

    [Test]
    public void Assign_WhileRunningAJob_HandsItBackWithoutTouchingTheRunningOne()
    {
        var state = new WorkerProtocolState();
        var running = Assignment();
        state.Assign(running);
        var seq = state.Snapshot().Seq;
        var second = Assignment();

        var handBack = state.Assign(second);

        Assert.That(handBack, Is.Not.Null);
        Assert.That(handBack!.Kind, Is.EqualTo(JobOutcomeKind.Rejected));
        Assert.That(handBack.AssignmentId, Is.EqualTo(second.AssignmentId));
        Assert.That(state.Snapshot().AssignmentId, Is.EqualTo(running.AssignmentId));
        Assert.That(state.Snapshot().Seq, Is.EqualTo(seq), "nothing about the worker changed");
        Assert.That(state.PendingOutcomes, Is.EqualTo(new[] { handBack }));
    }

    [Test]
    public void Assign_WhileRunningADiagnostic_HandsItBack()
    {
        var state = new WorkerProtocolState();
        Assert.That(state.TryStartDiagnostic(unprompted: true), Is.True);

        var handBack = state.Assign(Assignment());

        Assert.That(handBack?.Kind, Is.EqualTo(JobOutcomeKind.Rejected));
        Assert.That(state.Snapshot().Activity, Is.EqualTo(WorkerActivity.RunningDiagnostic));
    }

    [Test]
    public void FinishJob_RecordsTheOutcomeAndGoesIdle_InOneStep()
    {
        var state = new WorkerProtocolState();
        var job = Assignment();
        state.Assign(job);
        var seq = state.Snapshot().Seq;

        var outcome = state.FinishJob(JobStatus.Success, TimeSpan.FromSeconds(3), []);

        Assert.That(outcome.Kind, Is.EqualTo(JobOutcomeKind.Completed));
        Assert.That(outcome.Status, Is.EqualTo(JobStatus.Success));
        Assert.That(outcome.AssignmentId, Is.EqualTo(job.AssignmentId));
        Assert.That(state.PendingOutcomes, Is.EqualTo(new[] { outcome }));
        var after = state.Snapshot();
        Assert.That(after.Activity, Is.EqualTo(WorkerActivity.Idle));
        Assert.That(after.JobId, Is.Null);
        Assert.That(after.Seq, Is.GreaterThan(seq));
    }

    [Test]
    public void FinishJob_WhenNoJobIsRunning_Throws()
    {
        var state = new WorkerProtocolState();

        Assert.Throws<InvalidOperationException>(() => state.FinishJob(JobStatus.Success, TimeSpan.Zero, []));
    }

    [Test]
    public void Acknowledge_RemovesOnlyThoseOutcomes()
    {
        var state = new WorkerProtocolState();
        state.Assign(Assignment());
        var handBack = state.Assign(Assignment())!;
        var completed = state.FinishJob(JobStatus.Failed, TimeSpan.Zero, []);

        state.Acknowledge([handBack]);

        Assert.That(state.PendingOutcomes, Is.EqualTo(new[] { completed }));
    }

    [Test]
    public void RequestDiagnostic_WhenIdle_StartsOne()
    {
        var state = new WorkerProtocolState();

        Assert.That(state.RequestDiagnostic(), Is.True);
        Assert.That(state.Snapshot().Activity, Is.EqualTo(WorkerActivity.RunningDiagnostic));
    }

    // The server can't tell whether its request reached the worker before or after the running diagnostic started, so folding the request into it could leave the re-check unanswered.
    [Test]
    public void RequestDiagnostic_DuringADiagnostic_RunsAFreshOneAfterIt()
    {
        var state = new WorkerProtocolState();
        state.TryStartDiagnostic(unprompted: true);

        Assert.That(state.RequestDiagnostic(), Is.False, "not now");
        state.FinishDiagnostic(DiagnosticStatus.Healthy);

        Assert.That(state.TryStartDiagnostic(unprompted: false), Is.True, "but straight after");
        Assert.That(state.TryStartDiagnostic(unprompted: false), Is.False, "and only once");
    }

    [Test]
    public void RequestDiagnostic_DuringAJob_RunsOneOnceTheJobEnds()
    {
        var state = new WorkerProtocolState();
        state.TryStartDiagnostic(unprompted: true);
        state.FinishDiagnostic(DiagnosticStatus.Healthy);
        state.Assign(Assignment());

        Assert.That(state.RequestDiagnostic(), Is.False);
        Assert.That(state.TryStartDiagnostic(unprompted: false), Is.False, "not while the job runs");
        state.FinishJob(JobStatus.Failed, TimeSpan.Zero, []);

        Assert.That(state.TryStartDiagnostic(unprompted: false), Is.True);
    }

    [Test]
    public void TryStartDiagnostic_Unprompted_RunsWhenIdleEvenWithNothingAskedFor()
    {
        var state = new WorkerProtocolState();

        Assert.That(state.TryStartDiagnostic(unprompted: false), Is.False);
        Assert.That(state.TryStartDiagnostic(unprompted: true), Is.True);
    }

    [Test]
    public void FinishDiagnostic_RecordsTheVerdictInTheState()
    {
        var state = new WorkerProtocolState();
        Assert.That(state.Snapshot().DiagnosticStatus, Is.Null, "no verdict before the first diagnostic");
        state.TryStartDiagnostic(unprompted: true);

        state.FinishDiagnostic(DiagnosticStatus.Unhealthy);

        Assert.That(state.Snapshot().Activity, Is.EqualTo(WorkerActivity.Idle));
        Assert.That(state.Snapshot().DiagnosticStatus, Is.EqualTo(DiagnosticStatus.Unhealthy));
    }

    // Shutting down is not a verdict on the host.
    [Test]
    public void FinishDiagnostic_WithoutAVerdict_KeepsThePreviousOne()
    {
        var state = new WorkerProtocolState();
        state.TryStartDiagnostic(unprompted: true);
        state.FinishDiagnostic(DiagnosticStatus.Healthy);
        state.TryStartDiagnostic(unprompted: true);

        state.FinishDiagnostic(null);

        Assert.That(state.Snapshot().DiagnosticStatus, Is.EqualTo(DiagnosticStatus.Healthy));
    }

    [Test]
    public void Snapshot_SeqOnlyEverIncreases()
    {
        var state = new WorkerProtocolState();
        var seqs = new[]
        {
            state.Snapshot().Seq,
            Step(() => state.TryStartDiagnostic(unprompted: true)),
            Step(() => state.FinishDiagnostic(DiagnosticStatus.Healthy)),
            Step(() => state.Assign(Assignment())),
            Step(() => state.FinishJob(JobStatus.Success, TimeSpan.Zero, []))
        };

        Assert.That(seqs, Is.Ordered.Ascending);
        Assert.That(seqs.Distinct().Count(), Is.EqualTo(seqs.Length));

        long Step(Action change)
        {
            change();
            return state.Snapshot().Seq;
        }
    }

    [Test]
    public void Assign_WhileHeld_HandsItBack_AndReleasingTakesTheNext()
    {
        var state = new WorkerProtocolState();
        state.SetHold(WorkerHold.ShuttingDown, true);

        var handBack = state.Assign(Assignment());

        Assert.That(handBack?.Kind, Is.EqualTo(JobOutcomeKind.Rejected));
        Assert.That(state.Snapshot().Activity, Is.EqualTo(WorkerActivity.Idle));

        state.SetHold(WorkerHold.ShuttingDown, false);
        Assert.That(state.Assign(Assignment()), Is.Null);
    }

    [Test]
    public void AHold_IsReported_AndChangesTheSeqOnlyWhenItChanges()
    {
        var state = new WorkerProtocolState();
        var before = state.Snapshot().Seq;

        Assert.That(state.SetHold(WorkerHold.ShuttingDown, true), Is.True);
        var held = state.Snapshot();
        Assert.That(held.Hold, Is.EqualTo(WorkerHold.ShuttingDown));
        Assert.That(held.Seq, Is.GreaterThan(before));

        Assert.That(state.SetHold(WorkerHold.ShuttingDown, true), Is.False, "already held");
        Assert.That(state.Snapshot().Seq, Is.EqualTo(held.Seq));

        Assert.That(state.SetHold(WorkerHold.ShuttingDown, false), Is.True);
        Assert.That(state.Snapshot().Hold, Is.Null);
        Assert.That(state.Snapshot().Seq, Is.GreaterThan(held.Seq));
    }

    [Test]
    public void AHold_LeavesARunningJobRunning()
    {
        var state = new WorkerProtocolState();
        var job = Assignment();
        state.Assign(job);

        state.SetHold(WorkerHold.ShuttingDown, true);

        Assert.That(state.Snapshot().Activity, Is.EqualTo(WorkerActivity.RunningJob));
        Assert.That(state.Snapshot().JobId, Is.EqualTo(job.Id));
    }

    private static JobAssignment Assignment()
    {
        return new JobAssignment
        {
            Id = Guid.NewGuid(),
            AssignmentId = Guid.NewGuid(),
            RepoUrl = "/repo",
            Ref = "main",
            ScriptPath = "pipeline.csx"
        };
    }
}
