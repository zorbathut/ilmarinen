using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Services;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
public class WorkspaceViewTests
{
    private static readonly IReadOnlyList<WorkspaceInfo> Listing =
    [
        new WorkspaceInfo { Name = "measured", Path = "/ws/measured", LastUsed = DateTimeOffset.UnixEpoch },
        new WorkspaceInfo { Name = "failed", Path = "/ws/failed", LastUsed = DateTimeOffset.UnixEpoch },
        new WorkspaceInfo { Name = "new", Path = "/ws/new", LastUsed = DateTimeOffset.UnixEpoch },
    ];

    [Test]
    public void Join_NoReport_NothingIsMeasured()
    {
        var views = WorkspaceView.Join(Listing, null);

        Assert.That(views.Select(v => v.Name), Is.EqualTo(Listing.Select(w => w.Name)));
        Assert.That(views, Has.All.Matches<WorkspaceView>(v => !v.Measured && v.SizeBytes == null));
    }

    [Test]
    public void Join_TellsMeasuredApartFromFailedAndFromNotYetMeasured()
    {
        var report = new WorkspaceUsageReport
        {
            WorkspaceSizes = new Dictionary<string, long?> { ["measured"] = 4096, ["failed"] = null, ["deleted"] = 8192 }
        };

        var views = WorkspaceView.Join(Listing, report).ToDictionary(v => v.Name);

        Assert.That(views["measured"].Measured, Is.True);
        Assert.That(views["measured"].SizeBytes, Is.EqualTo(4096));
        Assert.That(views["failed"].Measured, Is.True);
        Assert.That(views["failed"].SizeBytes, Is.Null);
        Assert.That(views["new"].Measured, Is.False);
        Assert.That(views.Keys, Is.EquivalentTo(Listing.Select(w => w.Name)), "a workspace only the report still mentions is gone");
    }
}
