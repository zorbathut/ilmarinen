using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using NUnit.Framework;
using System.Collections.Generic;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>How long the fleet takes to get through what is running and queued, by simulating the server's own dispatch.</summary>
[TestFixture]
public class QueueForecastTests
{
    // Worker ids, named for the role each plays in the tests that set them up with different priorities.
    private static readonly Guid Low = Guid.NewGuid();
    private static readonly Guid High = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private readonly Dictionary<(Guid, string, string), TimeSpan> _perWorker = new();
    private readonly Dictionary<(string, string), TimeSpan> _acrossWorkers = new();

    [Test]
    public void NothingRunningOrQueued_DrainsNow()
    {
        var estimate = Forecast([Worker(Other, StandardRank.Medium)], [], []);

        Assert.That(estimate.EstimatedDrainSeconds, Is.Zero);
        Assert.That(estimate.QueuedJobs + estimate.RunningJobs + estimate.UnestimatedJobs + estimate.UnsatisfiableJobs, Is.Zero);
    }

    [Test]
    public void OneIdleWorker_RunsTheQueueOneAfterAnother()
    {
        var estimate = Forecast([Worker(Other, StandardRank.Medium)], [], [Job("a", 2), Job("b", 3)]);

        Assert.That(estimate.EstimatedDrainSeconds, Is.EqualTo(5 * 60));
    }

    [Test]
    public void TwoIdleWorkers_RunTheQueueSideBySide()
    {
        var estimate = Forecast([Worker(Low, StandardRank.Medium), Worker(Other, StandardRank.Medium)], [], [Job("a", 2), Job("b", 3)]);

        Assert.That(estimate.EstimatedDrainSeconds, Is.EqualTo(3 * 60));
    }

    [Test]
    public void ABusyWorker_TakesQueuedWorkOnceItsJobIsDone()
    {
        var estimate = Forecast([Worker(Other, StandardRank.Medium)], [Running(Other, remaining: 4)], [Job("a", 2)]);

        Assert.That(estimate.EstimatedDrainSeconds, Is.EqualTo(6 * 60));
        Assert.That(estimate.RunningJobs, Is.EqualTo(1));
    }

    // Dispatch never holds a job back for a better worker.
    [Test]
    public void AWorkerFreeingFirst_TakesTheJobWithoutWaitingForAHigherPriorityOne()
    {
        var estimate = Forecast(
            [Worker(Low, StandardRank.Low), Worker(High, StandardRank.High)],
            [Running(High, remaining: 10)],
            [Job("a", 2)]);

        Assert.That(estimate.EstimatedDrainSeconds, Is.EqualTo(10 * 60), "the Low worker runs it meanwhile; the High one's own job is what takes longest");
    }

    [Test]
    public void TwoWorkersFreeTogether_TheHigherPriorityTakesTheOlderJob()
    {
        _perWorker[(High, "repo", "a")] = 10 * Minute;
        _perWorker[(Low, "repo", "a")] = 1 * Minute;
        _perWorker[(High, "repo", "b")] = 1 * Minute;
        _perWorker[(Low, "repo", "b")] = 1 * Minute;

        var estimate = Forecast([Worker(Low, StandardRank.Low), Worker(High, StandardRank.High)], [], [Job("a"), Job("b")]);

        Assert.That(estimate.EstimatedDrainSeconds, Is.EqualTo(10 * 60), "the High worker took the older job, the one it is slow at");
    }

    [Test]
    public void AJobNeedingAHigherPriority_WaitsForThatWorkerWhileOthersTakeWhatTheyCan()
    {
        var estimate = Forecast(
            [Worker(Low, StandardRank.Low), Worker(High, StandardRank.High)],
            [Running(High, remaining: 5)],
            [Job("needs-high", 2, StandardRank.High), Job("any", 3)]);

        Assert.That(estimate.EstimatedDrainSeconds, Is.EqualTo(7 * 60), "Low runs 'any' now; 'needs-high' starts when High frees up");
    }

    [Test]
    public void AJobNoAvailableWorkerQualifiesFor_IsCountedAndLeftOut()
    {
        var estimate = Forecast([Worker(Low, StandardRank.Low)], [], [Job("needs-high", 30, StandardRank.High), Job("any", 2)]);

        Assert.That(estimate.UnsatisfiableJobs, Is.EqualTo(1));
        Assert.That(estimate.EstimatedDrainSeconds, Is.EqualTo(2 * 60));
        Assert.That(estimate.QueuedJobs, Is.EqualTo(2), "still queued, just not going anywhere");
    }

    [Test]
    public void NoWorkers_LeavesEveryQueuedJobOut_ButStillCountsWhatIsRunning()
    {
        var estimate = Forecast([], [Running(workerId: null, remaining: 3)], [Job("a", 2)]);

        Assert.That(estimate.UnsatisfiableJobs, Is.EqualTo(1));
        Assert.That(estimate.EstimatedDrainSeconds, Is.EqualTo(3 * 60));
        Assert.That(estimate.AvailableWorkers, Is.Zero);
    }

    [Test]
    public void TheTakingWorkerHasNoHistory_FallsBackToOtherWorkers()
    {
        _acrossWorkers[("repo", "a")] = 4 * Minute;

        var estimate = Forecast([Worker(Other, StandardRank.Medium)], [], [Job("a")]);

        Assert.That(estimate.EstimatedDrainSeconds, Is.EqualTo(4 * 60));
        Assert.That(estimate.UnestimatedJobs, Is.Zero);
    }

    [Test]
    public void NoHistoryAnywhere_CountsAsUnestimated_ButStillTakesItsTurn()
    {
        var estimate = Forecast([Worker(Other, StandardRank.Medium)], [Running(Other, remaining: null)], [Job("new"), Job("known", 2)]);

        Assert.That(estimate.UnestimatedJobs, Is.EqualTo(2));
        Assert.That(estimate.EstimatedDrainSeconds, Is.EqualTo(2 * 60), "the unknown ones count as no time, so this is a lower bound");
    }

    // A worker's own history of the script beats the fleet's, for a running job as for a queued one.
    [Test]
    public void ARunningJob_UsesItsWorkersOwnEstimateBeforeTheFleets()
    {
        _perWorker[(Other, "repo", "build")] = 30 * Minute;
        _acrossWorkers[("repo", "build")] = 5 * Minute;

        var estimate = Forecast([Worker(Other, StandardRank.Medium)], [new QueueForecast.RunningJob(Other, "repo", "build", 10 * Minute)], []);

        Assert.That(estimate.EstimatedDrainSeconds, Is.EqualTo(20 * 60));
    }

    // A worker running a script for the first time has no history of its own; the fleet's says it isn't nearly done.
    [Test]
    public void ARunningJobOnAWorkerNewToTheScript_FallsBackToOtherWorkers()
    {
        _acrossWorkers[("repo", "build")] = 60 * Minute;

        var estimate = Forecast([Worker(Other, StandardRank.Medium)], [new QueueForecast.RunningJob(Other, "repo", "build", 10 * Minute)], [Job("a", 10)]);

        Assert.That(estimate.EstimatedDrainSeconds, Is.EqualTo(60 * 60));
        Assert.That(estimate.UnestimatedJobs, Is.Zero);
    }

    [Test]
    public void ARunningJobPastItsEstimate_IsAboutToFinish()
    {
        var estimate = Forecast([Worker(Other, StandardRank.Medium)], [Running(Other, remaining: -5)], [Job("a", 2)]);

        Assert.That(estimate.EstimatedDrainSeconds, Is.EqualTo(2 * 60));
    }

    // Disconnected mid-job: it still has to finish, but its worker isn't there to take anything else.
    [Test]
    public void ARunningJobWhoseWorkerIsntAvailable_CountsButOffersNoLane()
    {
        var estimate = Forecast([Worker(Other, StandardRank.Medium)], [Running(Guid.NewGuid(), remaining: 8)], [Job("a", 2), Job("b", 2)]);

        Assert.That(estimate.EstimatedDrainSeconds, Is.EqualTo(8 * 60));
        Assert.That(estimate.RunningJobs, Is.EqualTo(1));
    }

    private QueueEstimate Forecast(IReadOnlyList<AvailableWorker> workers, IReadOnlyList<QueueForecast.RunningJob> running, IReadOnlyList<QueueForecast.QueuedJob> queued)
    {
        return QueueForecast.Estimate(workers, connectedWorkers: workers.Count, running, queued, new ScriptEstimates(_perWorker, _acrossWorkers));
    }

    private static AvailableWorker Worker(Guid id, int priority)
    {
        return new AvailableWorker(id, priority);
    }

    /// <summary>A job running a script of its own that takes ten minutes, with the given minutes left; null for one nobody has an estimate for.</summary>
    private QueueForecast.RunningJob Running(Guid? workerId, double? remaining)
    {
        var script = $"running-{Guid.NewGuid():N}";
        if (remaining == null)
        {
            return new QueueForecast.RunningJob(workerId, "repo", script, TimeSpan.FromMinutes(1));
        }
        _acrossWorkers[("repo", script)] = 10 * Minute;
        return new QueueForecast.RunningJob(workerId, "repo", script, TimeSpan.FromMinutes(10 - remaining.Value));
    }

    /// <summary>A queued job that takes the given minutes on any worker (its script is named after it).</summary>
    private QueueForecast.QueuedJob Job(string script, double minutes, int? minimum = null)
    {
        _acrossWorkers[("repo", script)] = TimeSpan.FromMinutes(minutes);
        return new QueueForecast.QueuedJob(minimum, "repo", script);
    }

    /// <summary>A queued job nobody has an estimate for, that any worker may run.</summary>
    private static QueueForecast.QueuedJob Job(string script)
    {
        return new QueueForecast.QueuedJob(null, "repo", script);
    }
}
