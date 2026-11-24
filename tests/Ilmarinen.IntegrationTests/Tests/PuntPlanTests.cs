using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Server.Services;
using NUnit.Framework;
using System.Collections.Generic;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
public class PuntPlanTests
{
    private const long GiB = 1L << 30;

    private static readonly Guid Punter = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Other = new("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Third = new("00000000-0000-0000-0000-000000000003");

    private sealed record Job(string Name, int? MinWorkerRank = null, long NeedBytes = 0);

    private static readonly Job Punted = new("punted");

    private static DispatchJob Describe(Job job)
    {
        return new DispatchJob(job.MinWorkerRank, null, new DiskNeed(job.NeedBytes, job.NeedBytes));
    }

    private static DispatchWorker Worker(Guid id, int rank = 0, long? freeBytes = null)
    {
        var disk = freeBytes is { } free ? new DiskSpaceReport { FreeBytes = free, TotalBytes = 100 * GiB, MinFreeBytes = 0 } : null;
        return new DispatchWorker(rank, new WorkerDisk(id, disk, new Dictionary<string, long?>()));
    }

    private static PuntPrediction Predict(IReadOnlyList<Job> queue, IReadOnlyList<DispatchWorker> others, DispatchWorker? punterOnceIdle, bool draining = false, Job? punted = null)
    {
        return PuntPlan.Predict(queue, Describe, punted ?? Punted, others, punterOnceIdle, draining);
    }

    [Test]
    public void AnotherIdleWorker_TakesIt()
    {
        var prediction = Predict([Punted], [Worker(Other)], Worker(Punter));

        Assert.That(prediction, Is.EqualTo(new PuntPrediction(PuntOutcome.GoesElsewhere, Other, Draining: false)));
    }

    [Test]
    public void OnlyThisWorker_ItComesBack()
    {
        var prediction = Predict([Punted], [], Worker(Punter));

        Assert.That(prediction, Is.EqualTo(new PuntPrediction(PuntOutcome.ComesBack, Punter, Draining: false)));
    }

    [Test]
    public void AnotherIdleWorker_WithoutDiskRoom_DoesNotCount()
    {
        var big = new Job("big", NeedBytes: 50 * GiB);

        var prediction = Predict([big], [Worker(Other, freeBytes: 10 * GiB)], Worker(Punter), punted: big);

        Assert.That(prediction.Outcome, Is.EqualTo(PuntOutcome.ComesBack));
    }

    [Test]
    public void AnotherIdleWorker_BelowTheJobsMinimum_DoesNotCount()
    {
        var picky = new Job("picky", MinWorkerRank: 5);

        var prediction = Predict([picky], [Worker(Other, rank: 1)], Worker(Punter, rank: 5), punted: picky);

        Assert.That(prediction.Outcome, Is.EqualTo(PuntOutcome.ComesBack));
    }

    [Test]
    public void AJobAheadInTheQueue_TakesTheOnlyOtherWorker_SoItComesBack()
    {
        var ahead = new Job("ahead");

        var prediction = Predict([ahead, Punted], [Worker(Other)], Worker(Punter));

        Assert.That(prediction.Outcome, Is.EqualTo(PuntOutcome.ComesBack));
    }

    [Test]
    public void JobsAheadTakeEveryWorker_ItWaits()
    {
        var prediction = Predict([new Job("a"), new Job("b"), Punted], [Worker(Other)], Worker(Punter));

        Assert.That(prediction, Is.EqualTo(new PuntPrediction(PuntOutcome.Waits, null, Draining: false)));
    }

    [Test]
    public void TheWorkerWouldNotTakeItBack_ItWaits()
    {
        var prediction = Predict([Punted], [], punterOnceIdle: null);

        Assert.That(prediction, Is.EqualTo(new PuntPrediction(PuntOutcome.Waits, null, Draining: false)));
    }

    [Test]
    public void TheWorkerWouldNotTakeItBack_ButAnotherWould_ItGoesThere()
    {
        var prediction = Predict([Punted], [Worker(Other), Worker(Third, rank: 3)], punterOnceIdle: null);

        Assert.That(prediction, Is.EqualTo(new PuntPrediction(PuntOutcome.GoesElsewhere, Third, Draining: false)));
    }

    [Test]
    public void Draining_ItWaits_AndSaysWhy()
    {
        var prediction = Predict([Punted], [Worker(Other)], Worker(Punter), draining: true);

        Assert.That(prediction, Is.EqualTo(new PuntPrediction(PuntOutcome.Waits, null, Draining: true)));
    }
}
