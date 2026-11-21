using Ilmarinen.Protocol;
using Ilmarinen.Worker.Services;
using Ilmarinen.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.Core.Tests;

[TestFixture]
public class WorkspaceManagerTests
{
    private string _tempDir = null!;
    private WorkspaceManager _manager = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ws-manager-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var config = new WorkerConfig
        {
            ServerUrl = "http://localhost",
            WorkspacePath = _tempDir,
            WorkerKey = "test:01DUMMY00000000000000000000:AAAA:BBBB"
        };

        _manager = new WorkspaceManager(config, NullLogger<WorkspaceManager>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Test]
    public void DiscoverWorkspaces_ReturnsNamedDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "my-project"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "another-ws"));

        var result = _manager.DiscoverWorkspaces();
        var names = result.Select(w => w.Name).ToList();

        Assert.That(names, Is.EquivalentTo(new[] { "my-project", "another-ws" }));
        Assert.That(result, Has.All.Matches<Ilmarinen.Protocol.Responses.WorkspaceInfo>(
            w => w.Path == Path.GetFullPath(Path.Combine(_tempDir, w.Name))));
    }

    [Test]
    public void DiscoverWorkspaces_FiltersOutDotDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "my-project"));
        Directory.CreateDirectory(Path.Combine(_tempDir, ".mirrors"));

        var names = _manager.DiscoverWorkspaces().Select(w => w.Name).ToList();

        Assert.That(names, Is.EqualTo(new[] { "my-project" }));
    }

    [Test]
    public void DiscoverWorkspaces_FiltersOutGuidDirectories()
    {
        // GUID-named dirs are ephemeral (job IDs)
        Directory.CreateDirectory(Path.Combine(_tempDir, Guid.NewGuid().ToString()));
        Directory.CreateDirectory(Path.Combine(_tempDir, Guid.CreateVersion7().ToString()));
        // Named dirs are persistent
        Directory.CreateDirectory(Path.Combine(_tempDir, "real-workspace"));

        var result = _manager.DiscoverWorkspaces();

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.Select(w => w.Name), Does.Contain("real-workspace"));
    }

    [Test]
    public void DiscoverWorkspaces_EmptyDirectory_ReturnsEmpty()
    {
        var result = _manager.DiscoverWorkspaces();

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void DiscoverWorkspaces_NonExistentPath_ReturnsEmpty()
    {
        Directory.Delete(_tempDir);

        var result = _manager.DiscoverWorkspaces();

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void TryDelete_ActiveWorkspace_RefusesDeletion()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "active-ws"));
        _manager.SetActiveWorkspace("active-ws");

        var result = _manager.TryDelete("active-ws");

        Assert.That(result.Status, Is.EqualTo(DeleteWorkspaceStatus.InUse));
        Assert.That(result.Error, Does.Contain("in use"));
    }

    [Test]
    public void TryDelete_RemovesTheWorkspace_AndLeavesNothingAside()
    {
        var dir = Path.Combine(_tempDir, "doomed-ws");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "sub", "file.txt"), "x");
        File.SetAttributes(Path.Combine(dir, "sub", "file.txt"), FileAttributes.ReadOnly);

        var result = _manager.TryDelete("doomed-ws");

        Assert.That(result.Status, Is.EqualTo(DeleteWorkspaceStatus.Success));
        Assert.That(Directory.Exists(dir), Is.False);
        Assert.That(Directory.GetDirectories(_tempDir), Is.Empty, "the tree moved aside for deleting is gone too");
    }

    // The name is freed the moment the delete starts, by moving the tree aside: a job that starts on the workspace then makes it afresh instead of using a tree half deleted beneath it, and a delete that fails partway leaves its remains under a name no job uses.
    [Test]
    public void TryDelete_ThatCanNotDeleteEveryFile_StillFreesTheName()
    {
        if (Environment.UserName == "root")
        {
            Assert.Ignore("root deletes through any permissions, so there is no undeletable file to make");
        }
        var dir = Path.Combine(_tempDir, "stubborn-ws");
        var locked = Path.Combine(dir, "locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "file.txt"), "x");
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var result = _manager.TryDelete("stubborn-ws");

            Assert.That(result.Status, Is.EqualTo(DeleteWorkspaceStatus.Success), "the workspace is gone, whatever became of its files: the server must drop it");
            Assert.That(Directory.Exists(dir), Is.False, "no job may find a half-deleted tree under the workspace's name");
            Assert.That(_manager.DiscoverWorkspaces(), Is.Empty, "the remains are hidden from listings and from eviction");
            Assert.That(Directory.GetDirectories(_tempDir, ".deleting-*"), Has.Length.EqualTo(1), "left for a later sweep");
        }
        finally
        {
            foreach (var left in Directory.GetDirectories(_tempDir, "*", SearchOption.AllDirectories))
            {
                File.SetUnixFileMode(left, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    [Test]
    public void TryDelete_DeletesSymlinksAsThemselves_LeavingWhatTheyPointAtAlone()
    {
        var outside = Path.Combine(_tempDir, ".outside");
        Directory.CreateDirectory(outside);
        var precious = Path.Combine(outside, "precious.txt");
        File.WriteAllText(precious, "keep");
        File.SetAttributes(precious, FileAttributes.ReadOnly);
        var dir = Path.Combine(_tempDir, "linky-ws");
        Directory.CreateDirectory(dir);
        Directory.CreateSymbolicLink(Path.Combine(dir, "to-outside"), outside);
        File.CreateSymbolicLink(Path.Combine(dir, "to-precious"), precious);

        Assert.That(_manager.TryDelete("linky-ws").Status, Is.EqualTo(DeleteWorkspaceStatus.Success));

        Assert.That(Directory.Exists(dir), Is.False);
        Assert.That(File.ReadAllText(precious), Is.EqualTo("keep"));
        Assert.That(File.GetAttributes(precious).HasFlag(FileAttributes.ReadOnly), Is.True, "nothing reached through a link was touched");
    }

    [Test]
    public void TryDelete_RefusesAJobsBuildDirectory()
    {
        var buildDir = Path.Combine(_tempDir, Guid.NewGuid().ToString());
        Directory.CreateDirectory(buildDir);

        var result = _manager.TryDelete(Path.GetFileName(buildDir));

        Assert.That(result.Status, Is.EqualTo(DeleteWorkspaceStatus.Error));
        Assert.That(Directory.Exists(buildDir), Is.True, "a running ephemeral job uses it without marking it active");
    }

    [Test]
    public void SweepDeletions_RemovesTreesLeftByDeletesThatDidNotFinish()
    {
        var left = Path.Combine(_tempDir, ".deleting-0123");
        Directory.CreateDirectory(Path.Combine(left, "sub"));
        File.WriteAllText(Path.Combine(left, "sub", "file.txt"), "x");
        Directory.CreateDirectory(Path.Combine(_tempDir, "kept-ws"));
        Directory.CreateDirectory(Path.Combine(_tempDir, ".mirrors"));

        Assert.That(_manager.DiscoverWorkspaces().Select(w => w.Name), Is.EqualTo(new[] { "kept-ws" }));

        _manager.SweepDeletions();

        Assert.That(Directory.Exists(left), Is.False);
        Assert.That(Directory.Exists(Path.Combine(_tempDir, "kept-ws")), Is.True);
        Assert.That(Directory.Exists(Path.Combine(_tempDir, ".mirrors")), Is.True, "only the deletion remains are swept");
    }

    [Test]
    public void TryDelete_NonExistentWorkspace_ReturnsError()
    {
        var result = _manager.TryDelete("no-such-ws");

        Assert.That(result.Status, Is.EqualTo(DeleteWorkspaceStatus.NotFound));
        Assert.That(result.Error, Does.Contain("does not exist"));
    }

    [Test]
    public void TryDelete_InvalidName_ReturnsError()
    {
        var result = _manager.TryDelete("../escape");

        Assert.That(result.Status, Is.EqualTo(DeleteWorkspaceStatus.Error));
        Assert.That(result.Error, Does.Contain("Path separators"));
    }

    [Test]
    public void TryDelete_ValidWorkspace_DeletesDirectory()
    {
        var wsPath = Path.Combine(_tempDir, "deletable");
        Directory.CreateDirectory(wsPath);
        File.WriteAllText(Path.Combine(wsPath, "file.txt"), "content");

        var result = _manager.TryDelete("deletable");

        Assert.That(result.Status, Is.EqualTo(DeleteWorkspaceStatus.Success));
        Assert.That(Directory.Exists(wsPath), Is.False);
    }

    [Test]
    public void TryDelete_AfterClearingActiveWorkspace_Succeeds()
    {
        var wsPath = Path.Combine(_tempDir, "was-active");
        Directory.CreateDirectory(wsPath);

        _manager.SetActiveWorkspace("was-active");
        _manager.ReleaseActiveWorkspace();

        var result = _manager.TryDelete("was-active");

        Assert.That(result.Status, Is.EqualTo(DeleteWorkspaceStatus.Success));
        Assert.That(Directory.Exists(wsPath), Is.False);
    }

    private const int OneMegabyte = 1024 * 1024;

    [Test]
    public async Task MeasureUsage_CountsNestedFilesAndDotfiles()
    {
        var ws = Path.Combine(_tempDir, "sized");
        WriteRandomFile(Path.Combine(ws, "src", "deep", "a.bin"), OneMegabyte);
        WriteRandomFile(Path.Combine(ws, ".git", "objects", "b.bin"), OneMegabyte);

        var usage = await _manager.MeasureUsageAsync(CancellationToken.None);

        Assert.That(usage.WorkspaceSizes["sized"], Is.GreaterThanOrEqualTo(2L * OneMegabyte));
    }

    [Test]
    public async Task MeasureUsage_DoesNotFollowSymlinks()
    {
        var outside = Path.Combine(_tempDir, ".outside");
        WriteRandomFile(Path.Combine(outside, "big.bin"), OneMegabyte);
        var ws = Path.Combine(_tempDir, "linked");
        Directory.CreateDirectory(ws);
        File.CreateSymbolicLink(Path.Combine(ws, "file-link"), Path.Combine(outside, "big.bin"));
        Directory.CreateSymbolicLink(Path.Combine(ws, "dir-link"), outside);

        var usage = await _manager.MeasureUsageAsync(CancellationToken.None);

        Assert.That(usage.WorkspaceSizes["linked"], Is.Not.Null.And.LessThan((long)OneMegabyte));
    }

    [Test]
    public async Task MeasureUsage_FileSharedWithTheMirrors_CountsTowardTheMirrorsOnly()
    {
        var mirrored = Path.Combine(_tempDir, ".mirrors", "repo.git", "objects", "pack.bin");
        WriteRandomFile(mirrored, OneMegabyte);
        HardLink(mirrored, Path.Combine(_tempDir, "clone", ".git", "objects", "pack.bin"));

        var usage = await _manager.MeasureUsageAsync(CancellationToken.None);

        Assert.That(usage.WorkspaceSizes["clone"], Is.Not.Null.And.LessThan((long)OneMegabyte));
        Assert.That(usage.MirrorsSizeBytes, Is.GreaterThanOrEqualTo((long)OneMegabyte));
    }

    [Test]
    public async Task MeasureUsage_FileSharedBetweenTwoWorkspaces_CountsTowardNeither()
    {
        // What two clones of one mirror are left with once the mirror is rebuilt: deleting either frees none of it
        var first = Path.Combine(_tempDir, "first", "pack.bin");
        WriteRandomFile(first, OneMegabyte);
        HardLink(first, Path.Combine(_tempDir, "second", "pack.bin"));

        var usage = await _manager.MeasureUsageAsync(CancellationToken.None);

        Assert.That(usage.WorkspaceSizes["first"], Is.Not.Null.And.LessThan((long)OneMegabyte));
        Assert.That(usage.WorkspaceSizes["second"], Is.Not.Null.And.LessThan((long)OneMegabyte));
    }

    [Test]
    public async Task MeasureUsage_FileLinkedTwiceInsideOneWorkspace_CountsOnce()
    {
        var original = Path.Combine(_tempDir, "doubled", "a.bin");
        WriteRandomFile(original, OneMegabyte);
        HardLink(original, Path.Combine(_tempDir, "doubled", "b.bin"));

        var usage = await _manager.MeasureUsageAsync(CancellationToken.None);

        Assert.That(usage.WorkspaceSizes["doubled"], Is.InRange((long)OneMegabyte, 2L * OneMegabyte - 1));
    }

    [Test]
    public async Task MeasureUsage_NoMirrors_ReportsZeroMirrorSize()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "ws"));

        var usage = await _manager.MeasureUsageAsync(CancellationToken.None);

        Assert.That(usage.MirrorsSizeBytes, Is.EqualTo(0));
    }

    [Test]
    public async Task MeasureUsage_ReflectsChangesImmediately()
    {
        var ws = Path.Combine(_tempDir, "growing");
        Directory.CreateDirectory(ws);
        var before = (await _manager.MeasureUsageAsync(CancellationToken.None)).WorkspaceSizes["growing"];

        WriteRandomFile(Path.Combine(ws, "grown.bin"), OneMegabyte);

        Assert.That((await _manager.MeasureUsageAsync(CancellationToken.None)).WorkspaceSizes["growing"], Is.GreaterThanOrEqualTo(before + OneMegabyte));
    }

    [Test]
    public async Task MeasureUsage_UnreadableDirectory_ReportsNullForThatWorkspaceOnly()
    {
        if (Environment.UserName == "root")
        {
            Assert.Ignore("root reads everything, so there is no unreadable directory to make");
        }
        var locked = Path.Combine(_tempDir, "locked", "secret");
        Directory.CreateDirectory(locked);
        Directory.CreateDirectory(Path.Combine(_tempDir, "fine"));
        Directory.CreateDirectory(Path.Combine(_tempDir, ".mirrors"));
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var usage = await _manager.MeasureUsageAsync(CancellationToken.None);

            Assert.That(usage.WorkspaceSizes["locked"], Is.Null);
            Assert.That(usage.WorkspaceSizes["fine"], Is.Not.Null);
            Assert.That(usage.MirrorsSizeBytes, Is.Not.Null);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.That((await _manager.MeasureUsageAsync(CancellationToken.None)).WorkspaceSizes["locked"], Is.Not.Null);
    }

    [Test]
    public void ReleaseActiveWorkspace_RecordsTheWorkspacesLastUse()
    {
        var ws = Path.Combine(_tempDir, "used");
        Directory.CreateDirectory(ws);
        Directory.SetLastWriteTimeUtc(ws, DateTime.UtcNow - TimeSpan.FromDays(3));

        var before = DateTimeOffset.UtcNow;
        _manager.SetActiveWorkspace("used");
        _manager.ReleaseActiveWorkspace();
        var after = DateTimeOffset.UtcNow;

        Assert.That(LastUsedOf("used"), Is.InRange(before, after));
    }

    [Test]
    public void ReleaseActiveWorkspace_WorkspaceNeverCreated_LeavesNothingBehind()
    {
        _manager.SetActiveWorkspace("never-made");
        _manager.ReleaseActiveWorkspace();

        Assert.That(_manager.DiscoverWorkspaces(), Is.Empty);
    }

    [Test]
    public void DiscoverWorkspaces_WorkspaceNoJobHasReleased_ReportsWhenItLastChanged()
    {
        var ws = Path.Combine(_tempDir, "untouched");
        Directory.CreateDirectory(ws);
        var changed = DateTime.UtcNow - TimeSpan.FromDays(3);
        Directory.SetLastWriteTimeUtc(ws, changed);

        Assert.That(LastUsedOf("untouched"), Is.EqualTo(new DateTimeOffset(changed)).Within(TimeSpan.FromSeconds(1)));
    }

    private DateTimeOffset LastUsedOf(string name)
    {
        return _manager.DiscoverWorkspaces().Single(w => w.Name == name).LastUsed;
    }

    private static void WriteRandomFile(string path, int length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Random so a compressing filesystem can't store it in fewer blocks than its length
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
    }

    private static void HardLink(string existing, string link)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        using var process = Process.Start("ln", [existing, link]);
        process.WaitForExit();
        Assert.That(process.ExitCode, Is.EqualTo(0), "ln failed");
    }
}
