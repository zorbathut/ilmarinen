using Ilmarinen.Protocol.Requests;
using Ilmarinen.Server.Services;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>Which free worker takes which queued job.</summary>
[TestFixture]
public class DispatchPlanTests
{
    private const long Gb = 1024L * 1024 * 1024;

    /// <summary>A worker whose disk isn't known yet, so it has room for anything.</summary>
    private static DispatchWorker Worker(int rank, params string[] workspaces)
    {
        return new DispatchWorker(rank, new WorkerDisk(Guid.NewGuid(), null, workspaces.ToDictionary(n => n, _ => (long?)null)));
    }

    /// <summary>A worker with this much free, keeping 1 GB free, holding these workspaces of these sizes.</summary>
    private static DispatchWorker Disk(int rank, long freeGb, params (string Name, long Gb)[] workspaces)
    {
        var disk = new DiskSpaceReport { FreeBytes = freeGb * Gb, TotalBytes = 1000 * Gb, MinFreeBytes = Gb };
        return new DispatchWorker(rank, new WorkerDisk(Guid.NewGuid(), disk, workspaces.ToDictionary(w => w.Name, w => (long?)(w.Gb * Gb))));
    }

    private static DispatchJob Job(int? minimum, string? workspace = null, long reuseGb = 0, long freshGb = 0)
    {
        return new DispatchJob(minimum, workspace, new DiskNeed(reuseGb * Gb, freshGb * Gb));
    }

    [Test]
    public void Pick_HighestQualifyingWorker()
    {
        var low = Worker(0);
        var high = Worker(2);

        Assert.That(DispatchPlan.Pick(Job(null), [low, high])?.Worker, Is.EqualTo(high));
        Assert.That(DispatchPlan.Pick(Job(1), [low]), Is.Null);
        Assert.That(DispatchPlan.Pick(Job(2), [low, high])?.Worker, Is.EqualTo(high));
    }

    [Test]
    public void Pick_AtEqualPriority_TheWorkerHoldingTheWorkspace()
    {
        var holders = Enumerable.Range(0, 5).Select(_ => Worker(1, "ws")).ToList();
        var others = Enumerable.Range(0, 5).Select(_ => Worker(1, "other")).ToList();

        // Whatever the worker IDs, which otherwise break ties.
        foreach (var holder in holders)
        {
            Assert.That(DispatchPlan.Pick(Job(null, "ws"), [.. others, holder])?.Worker, Is.EqualTo(holder));
        }
    }

    [Test]
    public void Pick_AHigherPriorityWorker_BeatsTheHolder()
    {
        var holder = Worker(0, "ws");
        var higher = Worker(1);

        Assert.That(DispatchPlan.Pick(Job(null, "ws"), [holder, higher])?.Worker, Is.EqualTo(higher));
    }

    [Test]
    public void Pick_TheHolderMustStillQualify()
    {
        var holder = Worker(0, "ws");
        var qualified = Worker(2);

        Assert.That(DispatchPlan.Pick(Job(1, "ws"), [holder, qualified])?.Worker, Is.EqualTo(qualified));
    }

    [Test]
    public void Pick_SkipsAWorkerWithoutRoom_EvenOfHigherPriority()
    {
        var full = Disk(2, freeGb: 5);
        var roomy = Disk(0, freeGb: 50);

        Assert.That(DispatchPlan.Pick(Job(null, freshGb: 20), [full, roomy])?.Worker, Is.EqualTo(roomy));
        Assert.That(DispatchPlan.Pick(Job(null, freshGb: 100), [full, roomy]), Is.Null);
    }

    [Test]
    public void Pick_AtEqualPriority_RoomWithoutDeletingBeatsDeleting_ThenLeastDeleted()
    {
        var mustDeleteMore = Disk(1, freeGb: 1, ("a", 30));
        var mustDeleteLess = Disk(1, freeGb: 15, ("b", 30));
        var hasRoom = Disk(1, freeGb: 25);

        Assert.That(DispatchPlan.Pick(Job(null, freshGb: 20), [mustDeleteMore, mustDeleteLess, hasRoom])?.Worker, Is.EqualTo(hasRoom));
        var pick = DispatchPlan.Pick(Job(null, freshGb: 20), [mustDeleteMore, mustDeleteLess]);
        Assert.That(pick?.Worker, Is.EqualTo(mustDeleteLess));
        Assert.That(pick!.Fit.EvictBytes, Is.EqualTo(6 * Gb), "20 needed and 1 kept free, of 15 free");
    }

    [Test]
    public void Pick_AtEqualPriority_TheTightestFit_KeepingTheRoomiestDiskForBiggerJobs()
    {
        var roomy = Disk(1, freeGb: 500);
        var snug = Disk(1, freeGb: 30);

        Assert.That(DispatchPlan.Pick(Job(null, freshGb: 20), [roomy, snug])?.Worker, Is.EqualTo(snug));
    }

    [Test]
    public void Pick_TheHolderThatCanReuse_BeatsOneWithMoreRoom()
    {
        var holder = Disk(1, freeGb: 10, ("ws", 40));
        var roomy = Disk(1, freeGb: 500);

        var pick = DispatchPlan.Pick(Job(null, "ws", reuseGb: 5, freshGb: 45), [roomy, holder]);

        Assert.That(pick?.Worker, Is.EqualTo(holder));
        Assert.That(pick!.Fit.StartFresh, Is.False);
    }

    [Test]
    public void Match_AJobWithoutRoomIsPassedOver_AndASmallerOneTakesTheWorker()
    {
        var worker = Disk(0, freeGb: 10);

        var matched = DispatchPlan.Match(new[] { "big", "small" }, j => j == "big" ? Job(null, freshGb: 50) : Job(null, freshGb: 2), [worker]);

        Assert.That(matched.Select(m => m.Job), Is.EqualTo(new[] { "small" }));
    }

    [Test]
    public void Match_SkipsAJobNobodyQualifiesFor_AndGivesTheWorkerToALaterOne()
    {
        var low = Worker(0);

        var matched = DispatchPlan.Match(new int?[] { 2, null }, m => Job(m), [low]);

        Assert.That(matched, Has.Count.EqualTo(1));
        Assert.That(matched[0].Job, Is.Null);
        Assert.That(matched[0].Worker, Is.EqualTo(low));
    }

    /// <summary>The rule dispatch followed before jobs were offered one at a time: the highest-priority free worker takes the first job it qualifies for, then the next worker.</summary>
    private static List<(int Job, Guid Worker)> WorkerByWorker(IReadOnlyList<int?> minimums, IReadOnlyList<DispatchWorker> workers)
    {
        var left = Enumerable.Range(0, minimums.Count).ToList();
        var matched = new List<(int, Guid)>();
        foreach (var worker in workers.OrderByDescending(w => w.Rank).ThenBy(w => w.WorkerId))
        {
            var job = left.Cast<int?>().FirstOrDefault(j => DispatchPlan.Qualifies(worker.Rank, minimums[j!.Value]));
            if (job is { } taken)
            {
                left.Remove(taken);
                matched.Add((taken, worker.WorkerId));
            }
        }
        return matched;
    }

    [Test]
    public void Match_IsTheSameMatchingAsWorkerByWorker()
    {
        var random = new Random(1234);
        for (var trial = 0; trial < 2000; trial++)
        {
            var workers = Enumerable.Range(0, random.Next(0, 6)).Select(_ => Worker(random.Next(0, 4))).ToList();
            var minimums = Enumerable.Range(0, random.Next(0, 8)).Select(_ => random.Next(0, 5) is var r && r == 4 ? (int?)null : r).ToList();

            var jobMajor = DispatchPlan.Match(Enumerable.Range(0, minimums.Count), j => Job(minimums[j]), workers)
                .Select(m => (m.Job, m.Worker.WorkerId))
                .OrderBy(m => m.Job)
                .ToList();
            var workerMajor = WorkerByWorker(minimums, workers).OrderBy(m => m.Job).ToList();

            Assert.That(jobMajor, Is.EqualTo(workerMajor), $"trial {trial}: minimums [{string.Join(", ", minimums)}], ranks [{string.Join(", ", workers.Select(w => w.Rank))}]");
        }
    }
}
