using Ilmarinen.Protocol.Responses;
using Ilmarinen.Worker.Services;
using Ilmarinen.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.Core.Tests;

/// <summary>The worker making room for a job before it starts, on a disk whose free space follows what is deleted.</summary>
[TestFixture]
public class RoomMakerTests
{
    private const long Floor = 1_000;

    private string _root = null!;
    private WorkerConfig _config = null!;
    private WorkspaceManager _workspaces = null!;

    /// <summary>Free space is a base amount plus a fixed size for each of the given workspaces that has been deleted.</summary>
    private sealed class Disk : IDiskSpace
    {
        private readonly string _root;
        private readonly long _free;
        private readonly (string Name, long Bytes)[] _sizes;

        public Disk(string root, long free, params (string Name, long Bytes)[] sizes)
        {
            _root = root;
            _free = free;
            _sizes = sizes;
        }

        public DiskReading? Read(string path)
        {
            return new DiskReading(_free + _sizes.Where(s => !Directory.Exists(Path.Combine(_root, s.Name))).Sum(s => s.Bytes), 1_000_000_000);
        }
    }

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"room-maker-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _config = new WorkerConfig { ServerUrl = "http://localhost", WorkspacePath = _root, WorkerKey = "test:01DUMMY00000000000000000000:AAAA:BBBB", MinFreeBytes = Floor };
        _workspaces = new WorkspaceManager(_config, NullLogger<WorkspaceManager>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>A workspace with a file of this size in it, last used this many days ago.</summary>
    private void Workspace(string name, int bytes, int daysAgo)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "data"), new byte[bytes]);
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddDays(-daysAgo));
    }

    private static JobAssignment Job(long needed, string? workspace = null, bool startFresh = false, long reuse = 0)
    {
        return new JobAssignment { Id = Guid.NewGuid(), AssignmentId = Guid.NewGuid(), RepoUrl = "r", Ref = "main", ScriptPath = "p.csx", DiskNeededBytes = needed, PredictedWorkspace = workspace, StartFresh = startFresh, DiskNeededToReuseBytes = reuse };
    }

    [Test]
    public async Task Evicts_LeastRecentlyUsedFirst_NeverTheJobsOwn_AndStopsOnceThereIsRoom()
    {
        Workspace("own", 50_000, daysAgo: 9);
        Workspace("oldest", 50_000, daysAgo: 7);
        Workspace("newer", 50_000, daysAgo: 1);
        await _workspaces.MeasureUsageAsync(CancellationToken.None);
        var maker = new RoomMaker(_config, _workspaces, new Disk(_root, 500, ("own", 50_000), ("oldest", 50_000), ("newer", 50_000)));

        var deleted = maker.MakeRoom(Job(20_000, workspace: "own"), NullLogger.Instance);

        Assert.That(deleted.Select(d => d.Name), Is.EqualTo(new[] { "oldest" }));
        Assert.That(deleted.Single().Result.Status, Is.EqualTo(Ilmarinen.Protocol.DeleteWorkspaceStatus.Success));
        Assert.That(Directory.Exists(Path.Combine(_root, "own")), Is.True);
        Assert.That(Directory.Exists(Path.Combine(_root, "newer")), Is.True);
    }

    [Test]
    public async Task RoomAlready_DeletesNothing()
    {
        Workspace("old", 50_000, daysAgo: 7);
        await _workspaces.MeasureUsageAsync(CancellationToken.None);
        var maker = new RoomMaker(_config, _workspaces, new Disk(_root, 100_000, ("old", 50_000)));

        Assert.That(maker.MakeRoom(Job(20_000), NullLogger.Instance), Is.Empty);
    }

    [Test]
    public async Task StartFresh_DeletesTheJobsOwnCopy_WhenReuseDoesntFit()
    {
        Workspace("own", 500_000, daysAgo: 1);
        await _workspaces.MeasureUsageAsync(CancellationToken.None);
        var maker = new RoomMaker(_config, _workspaces, new Disk(_root, 500, ("own", 500_000)));

        var deleted = maker.MakeRoom(Job(20_000, workspace: "own", startFresh: true, reuse: 100_000), NullLogger.Instance);

        Assert.That(deleted.Select(d => d.Name), Is.EqualTo(new[] { "own" }));
    }

    [Test]
    public async Task StartFresh_KeepsTheJobsOwnCopy_WhenTheWorkersOwnReadingSaysReuseFitsAfterAll()
    {
        Workspace("own", 500_000, daysAgo: 1);
        await _workspaces.MeasureUsageAsync(CancellationToken.None);
        // The server judged from an older reading; there is room to reuse now.
        var maker = new RoomMaker(_config, _workspaces, new Disk(_root, 200_000, ("own", 500_000)));

        var deleted = maker.MakeRoom(Job(20_000, workspace: "own", startFresh: true, reuse: 100_000), NullLogger.Instance);

        Assert.That(deleted, Is.Empty);
        Assert.That(Directory.Exists(Path.Combine(_root, "own")), Is.True);
    }
}
