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
    private static DispatchWorker Worker(int rank, params string[] workspaces)
    {
        return new DispatchWorker(Guid.NewGuid(), rank, workspaces.ToHashSet());
    }

    private static DispatchJob Job(int? minimum, string? workspace = null)
    {
        return new DispatchJob(minimum, workspace);
    }

    [Test]
    public void Pick_HighestQualifyingWorker()
    {
        var low = Worker(0);
        var high = Worker(2);

        Assert.That(DispatchPlan.Pick(Job(null), [low, high]), Is.EqualTo(high));
        Assert.That(DispatchPlan.Pick(Job(1), [low]), Is.Null);
        Assert.That(DispatchPlan.Pick(Job(2), [low, high]), Is.EqualTo(high));
    }

    [Test]
    public void Pick_AtEqualPriority_TheWorkerHoldingTheWorkspace()
    {
        var holders = Enumerable.Range(0, 5).Select(_ => Worker(1, "ws")).ToList();
        var others = Enumerable.Range(0, 5).Select(_ => Worker(1, "other")).ToList();

        // Whatever the worker IDs, which otherwise break ties.
        foreach (var holder in holders)
        {
            Assert.That(DispatchPlan.Pick(Job(null, "ws"), [.. others, holder]), Is.EqualTo(holder));
        }
    }

    [Test]
    public void Pick_AHigherPriorityWorker_BeatsTheHolder()
    {
        var holder = Worker(0, "ws");
        var higher = Worker(1);

        Assert.That(DispatchPlan.Pick(Job(null, "ws"), [holder, higher]), Is.EqualTo(higher));
    }

    [Test]
    public void Pick_TheHolderMustStillQualify()
    {
        var holder = Worker(0, "ws");
        var qualified = Worker(2);

        Assert.That(DispatchPlan.Pick(Job(1, "ws"), [holder, qualified]), Is.EqualTo(qualified));
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
