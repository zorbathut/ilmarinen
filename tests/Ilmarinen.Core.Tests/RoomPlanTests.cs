using Ilmarinen.Worker.Services;
using NUnit.Framework;
using System.Linq;
using System;

namespace Ilmarinen.Core.Tests;

/// <summary>Which workspaces a worker deletes to make room for a job.</summary>
[TestFixture]
public class RoomPlanTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-21T12:00:00Z");

    private static RoomCandidate Ws(string name, int daysAgo, long? gb)
    {
        return new RoomCandidate(name, Now.AddDays(-daysAgo), gb * Gb);
    }

    [Test]
    public void LeastRecentlyUsedFirst_OnlyAsManyAsItTakes()
    {
        var chosen = RoomPlan.Choose([Ws("recent", 1, 10), Ws("oldest", 9, 10), Ws("old", 5, 10)], 15 * Gb);

        Assert.That(chosen.Select(c => c.Name), Is.EqualTo(new[] { "oldest", "old" }));
    }

    [Test]
    public void NothingShort_DeletesNothing()
    {
        Assert.That(RoomPlan.Choose([Ws("a", 3, 10)], 0), Is.Empty);
    }

    [Test]
    public void AnUnmeasuredWorkspace_CountsAsFreeingNothing()
    {
        var chosen = RoomPlan.Choose([Ws("unmeasured", 9, null), Ws("measured", 5, 20), Ws("recent", 1, 20)], 15 * Gb);

        Assert.That(chosen.Select(c => c.Name), Is.EqualTo(new[] { "unmeasured", "measured" }));
    }

    [Test]
    public void NotEnoughEvenWithAll_ListsAll()
    {
        Assert.That(RoomPlan.Choose([Ws("a", 3, 1), Ws("b", 2, 1)], 50 * Gb), Has.Count.EqualTo(2));
    }
}
