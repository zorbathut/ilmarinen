using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using NUnit.Framework;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>What a script's next run will want of a worker's disk, from its latest runs.</summary>
[TestFixture]
public class DiskEstimateTests
{
    private static DiskRun Named(string name, long used = 0, bool fresh = false, bool stopped = false)
    {
        return new DiskRun(WorkspaceKind.Named, name, fresh, used, stopped);
    }

    private static readonly DiskRun Ephemeral = new(WorkspaceKind.Ephemeral, null, false, 0, false);

    [Test]
    public void PredictedWorkspace_IsTheLatestRunsWorkspace()
    {
        Assert.That(DiskEstimate.PredictedWorkspace([Named("new"), Named("old")]), Is.EqualTo("new"));
        Assert.That(DiskEstimate.PredictedWorkspace([Ephemeral, Named("old")]), Is.Null, "the script stopped using one");
        Assert.That(DiskEstimate.PredictedWorkspace([]), Is.Null);
    }
}
