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
    [Test]
    public void Pick_HighestQualifyingWorker()
    {
        var low = new DispatchWorker(Guid.NewGuid(), 0);
        var high = new DispatchWorker(Guid.NewGuid(), 2);

        Assert.That(DispatchPlan.Pick(null, [low, high]), Is.EqualTo(high));
        Assert.That(DispatchPlan.Pick(1, [low]), Is.Null);
        Assert.That(DispatchPlan.Pick(2, [low, high]), Is.EqualTo(high));
    }

    [Test]
    public void Match_SkipsAJobNobodyQualifiesFor_AndGivesTheWorkerToALaterOne()
    {
        var low = new DispatchWorker(Guid.NewGuid(), 0);

        var matched = DispatchPlan.Match(new int?[] { 2, null }, m => m, [low]);

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
            var workers = Enumerable.Range(0, random.Next(0, 6)).Select(_ => new DispatchWorker(Guid.NewGuid(), random.Next(0, 4))).ToList();
            var minimums = Enumerable.Range(0, random.Next(0, 8)).Select(_ => random.Next(0, 5) is var r && r == 4 ? (int?)null : r).ToList();

            var jobMajor = DispatchPlan.Match(Enumerable.Range(0, minimums.Count), j => minimums[j], workers)
                .Select(m => (m.Job, m.Worker.WorkerId))
                .OrderBy(m => m.Job)
                .ToList();
            var workerMajor = WorkerByWorker(minimums, workers).OrderBy(m => m.Job).ToList();

            Assert.That(jobMajor, Is.EqualTo(workerMajor), $"trial {trial}: minimums [{string.Join(", ", minimums)}], ranks [{string.Join(", ", workers.Select(w => w.Rank))}]");
        }
    }
}
