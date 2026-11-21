using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>What a script's next run will want of a worker's disk, from its latest runs.</summary>
[TestFixture]
public class DiskEstimateTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private const long NoCap = long.MaxValue;

    private static DiskRun Named(string name, long usedGb = 0, bool fresh = false, bool stopped = false, bool succeeded = true)
    {
        return new DiskRun(WorkspaceKind.Named, name, fresh, usedGb * Gb, stopped, succeeded && !stopped);
    }

    private static DiskRun EphemeralRun(long usedGb)
    {
        return new DiskRun(WorkspaceKind.Ephemeral, null, false, usedGb * Gb, false, true);
    }

    private static readonly DiskRun Ephemeral = EphemeralRun(0);

    private static WorkerDisk Worker(long? freeGb, params (string Name, long? Gb)[] workspaces)
    {
        var disk = freeGb is { } free ? new DiskSpaceReport { FreeBytes = free * Gb, TotalBytes = 1000 * Gb, MinFreeBytes = 5 * Gb } : null;
        return new WorkerDisk(Guid.NewGuid(), disk, workspaces.ToDictionary(w => w.Name, w => w.Gb * Gb));
    }

    [Test]
    public void PredictedWorkspace_IsTheLatestRunsWorkspace()
    {
        Assert.That(DiskEstimate.PredictedWorkspace([Named("new"), Named("old")]), Is.EqualTo("new"));
        Assert.That(DiskEstimate.PredictedWorkspace([Ephemeral, Named("old")]), Is.Null, "the script stopped using one");
        Assert.That(DiskEstimate.PredictedWorkspace([]), Is.Null);
    }

    [Test]
    public void Need_IsTheMostAnyRecentRunTook_NotATypicalOne()
    {
        var need = DiskEstimate.Need([Named("ws", 2), Named("ws", 30), Named("ws", 3)], 0, NoCap);

        Assert.That(need.ReuseBytes, Is.EqualTo(30 * Gb));
    }

    [Test]
    public void Need_ARunStoppedForDisk_CountsDouble()
    {
        var need = DiskEstimate.Need([Named("ws", 20, stopped: true), Named("ws", 30)], 0, NoCap);

        Assert.That(need.ReuseBytes, Is.EqualTo(40 * Gb));
    }

    [Test]
    public void Need_Afresh_PrefersAFreshRunThatWentTheDistance_ToOneThatFailedEarly()
    {
        var need = DiskEstimate.Need([Named("ws", 5), Named("ws", 1, fresh: true, succeeded: false), Named("ws", 20, fresh: true)], knownWorkspaceBytes: 50 * Gb, NoCap);

        Assert.That(need.FreshBytes, Is.EqualTo(20 * Gb));
        Assert.That(DiskEstimate.Need([Named("ws", 5), Named("ws", 1, fresh: true, succeeded: false)], 50 * Gb, NoCap).FreshBytes, Is.EqualTo(1 * Gb), "too little, if so, gets stopped for disk and counts double next time");
    }

    [Test]
    public void Need_Afresh_WithNoFreshRunAtAll_IsReuseAndTheWorkspace()
    {
        var need = DiskEstimate.Need([Named("ws", 5), Named("ws", 3)], knownWorkspaceBytes: 50 * Gb, NoCap);

        Assert.That(need, Is.EqualTo(new DiskNeed(5 * Gb, 55 * Gb)));
    }

    [Test]
    public void Need_ARunStoppedBeforeUsingAnything_DoesntCountAsGoingTheDistance()
    {
        var need = DiskEstimate.Need([Named("ws", 0, fresh: true, stopped: true), Named("ws", 20, fresh: true, succeeded: false)], 0, NoCap);

        Assert.That(need.FreshBytes, Is.EqualTo(20 * Gb));
    }

    [Test]
    public void Need_Afresh_IsWhatAFreshRunThatWentTheDistanceTook_EvenBelowTheWorkspacesGrownSize()
    {
        // A persistent workspace gathers build output run after run; making it afresh doesn't make all that again.
        var need = DiskEstimate.Need([Named("ws", 2), Named("ws", 20, fresh: true)], knownWorkspaceBytes: 53 * Gb, NoCap);

        Assert.That(need, Is.EqualTo(new DiskNeed(2 * Gb, 20 * Gb)));
    }

    [Test]
    public void Need_OnlyFreshRuns_ServeForReuseToo()
    {
        var need = DiskEstimate.Need([Named("ws", 12, fresh: true)], 0, NoCap);

        Assert.That(need, Is.EqualTo(new DiskNeed(12 * Gb, 12 * Gb)));
    }

    [Test]
    public void Need_OnlyRunsInThePredictedWorkspace_Count()
    {
        var need = DiskEstimate.Need([Named("new", 3), Named("old", 90), EphemeralRun(80)], 0, NoCap);

        Assert.That(need.ReuseBytes, Is.EqualTo(3 * Gb));
    }

    [Test]
    public void Need_Ephemeral_IsTheMostAnEphemeralRunTook()
    {
        var need = DiskEstimate.Need([EphemeralRun(4), EphemeralRun(9), Named("old", 90)], 0, NoCap);

        Assert.That(need.FreshBytes, Is.EqualTo(9 * Gb));
    }

    [Test]
    public void Need_NoHistory_IsNothing()
    {
        Assert.That(DiskEstimate.Need([], 0, NoCap), Is.EqualTo(DiskNeed.None));
        Assert.That(DiskEstimate.Need([new DiskRun(WorkspaceKind.Named, "ws", false, null, false, true)], 0, NoCap), Is.EqualTo(DiskNeed.None), "unmeasured");
    }

    [Test]
    public void Need_IsCappedAtTheMostAnyWorkerCouldGive_SoAnOutlierCantBlockAScriptForGood()
    {
        var need = DiskEstimate.Need([Named("ws", 900), Named("ws", 10)], 0, capBytes: 100 * Gb);

        Assert.That(need.ReuseBytes, Is.EqualTo(100 * Gb));
    }

    [Test]
    public void NeedOnFleet_CapsAtTheMostRoomAnyWorkerCouldMake_AndKnowsTheWorkspacesSize()
    {
        IReadOnlyCollection<WorkerDisk> fleet = [Worker(20, ("ws", 40)), Worker(100), Worker(null, ("ws", 70))];

        var need = DiskEstimate.NeedOnFleet([Named("ws", 900), Named("ws", 10)], fleet);

        Assert.That(need.ReuseBytes, Is.EqualTo(95 * Gb), "100 free less the 5 kept free");
        Assert.That(need.FreshBytes, Is.EqualTo(95 * Gb));
        Assert.That(DiskEstimate.NeedOnFleet([Named("ws", 3)], fleet).FreshBytes, Is.EqualTo(73 * Gb), "the largest copy of the workspace anywhere, on top");
    }

    [Test]
    public void NeedOnFleet_ACappedNeed_StillFitsSomewhere()
    {
        var holder = Worker(20, ("ws", 40), ("other", 30));
        var roomy = Worker(60);
        IReadOnlyCollection<WorkerDisk> fleet = [holder, roomy];

        var need = DiskEstimate.NeedOnFleet([Named("ws", 900, stopped: true)], fleet);

        Assert.That(fleet.Any(w => DiskEstimate.Fit("ws", need, w) != null), Is.True);
    }

    [Test]
    public void Fit_Reuses_WhenThereIsRoomForThat()
    {
        var fit = DiskEstimate.Fit("ws", new DiskNeed(10 * Gb, 60 * Gb), Worker(20, ("ws", 50)));

        Assert.That(fit, Is.EqualTo(new DiskFit(Reuses: true, StartFresh: false, NeededBytes: 10 * Gb, EvictBytes: 0, LeftoverBytes: 5 * Gb)));
    }

    [Test]
    public void Fit_DeletesOtherWorkspaces_WhenItMust()
    {
        var fit = DiskEstimate.Fit("ws", new DiskNeed(20 * Gb, 60 * Gb), Worker(10, ("ws", 50), ("other", 30)));

        Assert.That(fit!.Reuses, Is.True);
        Assert.That(fit.EvictBytes, Is.EqualTo(15 * Gb));
    }

    [Test]
    public void Fit_StartsFresh_WhenOnlyDeletingItsOwnCopyMakesRoom()
    {
        // The workspace has grown until reusing it leaves no room; starting over frees it all.
        var fit = DiskEstimate.Fit("ws", new DiskNeed(10 * Gb, 30 * Gb), Worker(6, ("ws", 50)));

        Assert.That(fit!.StartFresh, Is.True);
        Assert.That(fit.Reuses, Is.False);
        Assert.That(fit.NeededBytes, Is.EqualTo(30 * Gb));
        Assert.That(fit.EvictBytes, Is.Zero, "its own copy is enough");
    }

    [Test]
    public void Fit_NoRoomEvenAfterDeletingEverything_IsNull()
    {
        Assert.That(DiskEstimate.Fit("ws", new DiskNeed(80 * Gb, 90 * Gb), Worker(10, ("ws", 50), ("other", 20))), Is.Null);
    }

    [Test]
    public void Fit_UnmeasuredWorkspaces_CantBeCountedOn()
    {
        Assert.That(DiskEstimate.Fit(null, new DiskNeed(0, 20 * Gb), Worker(10, ("other", null))), Is.Null);
    }

    [Test]
    public void Fit_AWorkerNotYetHeardFrom_FitsAnything()
    {
        var fit = DiskEstimate.Fit("ws", new DiskNeed(900 * Gb, 900 * Gb), Worker(null, ("ws", null)));

        Assert.That(fit, Is.Not.Null);
        Assert.That(fit!.Reuses, Is.True);
    }
}
