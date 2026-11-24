using Ilmarinen.Protocol;
using System.Collections.Generic;
using System.Linq;
using System;

namespace Ilmarinen.Server.Services;

/// <summary>Where a punted job would go: Outcome, the worker for GoesElsewhere and ComesBack, and whether a drain is why it waits.</summary>
public record PuntPrediction(PuntOutcome Outcome, Guid? WorkerId, bool Draining);

/// <summary>
/// Predicts where a job would go if it were punted now, by the matching dispatch itself runs (<see cref="DispatchPlan.Match"/>), so the two can't disagree about disk room, priorities or the jobs queued ahead of it.
/// </summary>
public static class PuntPlan
{
    /// <param name="queueInDispatchOrder">The queue as it would be with the punted job back in it, in dispatch order.</param>
    /// <param name="others">The ready workers besides the one the job is punted from.</param>
    /// <param name="punterOnceIdle">The worker it is punted from, as dispatch would see it once idle; null if it wouldn't be ready even then.</param>
    public static PuntPrediction Predict<T>(IReadOnlyList<T> queueInDispatchOrder, Func<T, DispatchJob> describe, T punted, IReadOnlyList<DispatchWorker> others, DispatchWorker? punterOnceIdle, bool draining) where T : notnull
    {
        if (draining)
        {
            return new PuntPrediction(PuntOutcome.Waits, null, Draining: true);
        }

        // The requeue dispatches at once, to the workers ready now; the one it leaves joins them only when it reports itself idle, moments later.
        var now = DispatchPlan.Match(queueInDispatchOrder, describe, others);
        if (WorkerFor(now, punted) is { } elsewhere)
        {
            return new PuntPrediction(PuntOutcome.GoesElsewhere, elsewhere.WorkerId, Draining: false);
        }

        // That is long before a busy worker frees up or a sleeper wakes, so if the worker it leaves would take it then, that is where it goes.
        if (punterOnceIdle != null)
        {
            var taken = now.Select(m => m.Job).ToHashSet();
            var used = now.Select(m => m.Worker).ToHashSet();
            var remaining = queueInDispatchOrder.Where(j => !taken.Contains(j)).ToList();
            var then = DispatchPlan.Match(remaining, describe, [.. others.Where(w => !used.Contains(w)), punterOnceIdle]);
            if (WorkerFor(then, punted) is { } back && back.WorkerId == punterOnceIdle.WorkerId)
            {
                return new PuntPrediction(PuntOutcome.ComesBack, punterOnceIdle.WorkerId, Draining: false);
            }
        }

        return new PuntPrediction(PuntOutcome.Waits, null, Draining: false);
    }

    private static DispatchWorker? WorkerFor<T>(IReadOnlyList<(T Job, DispatchWorker Worker)> matching, T job) where T : notnull
    {
        return matching.Where(m => EqualityComparer<T>.Default.Equals(m.Job, job)).Select(m => m.Worker).FirstOrDefault();
    }
}
