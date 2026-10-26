using Ilmarinen.Worker.Services;
using NUnit.Framework;
using System.IO;
using System;
using static Ilmarinen.Core.Tests.GitTestRepos;

namespace Ilmarinen.Core.Tests;

[TestFixture]
public class WorkspaceGitHelperTests
{
    private string _root = null!;
    private string _remoteDir = null!;
    private string _workspaceDir = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"wgh-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _remoteDir = Path.Combine(_root, "remote");
        _workspaceDir = Path.Combine(_root, "workspace");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            ForceDeleteDirectory(_root);
        }
    }

    [Test]
    public void PrepareWorkspace_ForceUpdatesDivergedTag()
    {
        // Remote: c1 tagged v1.0
        InitRemote();
        var c1 = WriteAndCommit(_remoteDir, "file.txt", "v1");
        TagAt(_remoteDir, "v1.0", c1);

        // Initial clone of workspace
        WorkspaceGitHelper.PrepareWorkspace(_workspaceDir, _remoteDir, c1);
        Assert.That(LookupTag(_workspaceDir, "v1.0"), Is.EqualTo(c1));

        // Remote moves v1.0 to a new commit c2 (forced retag)
        var c2 = WriteAndCommit(_remoteDir, "file.txt", "v2");
        DeleteTag(_remoteDir, "v1.0");
        TagAt(_remoteDir, "v1.0", c2);

        // Workspace runs again, pinned to c2
        WorkspaceGitHelper.PrepareWorkspace(_workspaceDir, _remoteDir, c2);

        Assert.That(LookupTag(_workspaceDir, "v1.0"), Is.EqualTo(c2),
            "Tag v1.0 should be force-updated to the new commit on the remote.");
    }

    [Test]
    public void PrepareWorkspace_ForceUpdatesDivergedBranch()
    {
        // Remote: master at c1, plus branch 'feature' at c1
        InitRemote();
        var c1 = WriteAndCommit(_remoteDir, "file.txt", "v1");
        CreateBranch(_remoteDir, "feature", c1);

        WorkspaceGitHelper.PrepareWorkspace(_workspaceDir, _remoteDir, c1);
        Assert.That(LookupRemoteBranch(_workspaceDir, "feature"), Is.EqualTo(c1));

        // Remote rewrites 'feature' to point at a new commit c2 (non-fast-forward)
        var c2 = WriteAndCommit(_remoteDir, "other.txt", "x");
        UpdateBranch(_remoteDir, "feature", c2);

        // Run again pinned to master tip (c2 is master tip after second commit)
        WorkspaceGitHelper.PrepareWorkspace(_workspaceDir, _remoteDir, c2);

        Assert.That(LookupRemoteBranch(_workspaceDir, "feature"), Is.EqualTo(c2),
            "Remote-tracking branch origin/feature should be force-updated.");
    }

    [Test]
    public void PrepareWorkspace_PrunesDeletedBranch()
    {
        InitRemote();
        var c1 = WriteAndCommit(_remoteDir, "file.txt", "v1");
        CreateBranch(_remoteDir, "feature", c1);

        WorkspaceGitHelper.PrepareWorkspace(_workspaceDir, _remoteDir, c1);
        Assert.That(LookupRemoteBranch(_workspaceDir, "feature"), Is.EqualTo(c1));

        // Remote deletes 'feature'
        DeleteBranch(_remoteDir, "feature");

        WorkspaceGitHelper.PrepareWorkspace(_workspaceDir, _remoteDir, c1);

        Assert.That(LookupRemoteBranch(_workspaceDir, "feature"), Is.Null,
            "Stale remote-tracking branch should be pruned.");
    }

    [Test]
    public void PrepareWorkspace_OverwritesUntrackedFileCollidingWithNewCommit()
    {
        InitRemote();
        var c1 = WriteAndCommit(_remoteDir, "file.txt", "v1");
        WorkspaceGitHelper.PrepareWorkspace(_workspaceDir, _remoteDir, c1);

        // A previous run left untracked files (e.g. build-generated sidecars); the remote then adds a tracked file at one of those paths
        File.WriteAllText(Path.Combine(_workspaceDir, "generated.txt"), "stale local content");
        File.WriteAllText(Path.Combine(_workspaceDir, "cache.txt"), "build cache");
        var c2 = WriteAndCommit(_remoteDir, "generated.txt", "tracked content");

        WorkspaceGitHelper.PrepareWorkspace(_workspaceDir, _remoteDir, c2);

        Assert.That(File.ReadAllText(Path.Combine(_workspaceDir, "generated.txt")), Is.EqualTo("tracked content"),
            "Checkout should overwrite the untracked file with the tracked version from the target commit.");
        Assert.That(File.ReadAllText(Path.Combine(_workspaceDir, "cache.txt")), Is.EqualTo("build cache"),
            "Non-colliding untracked files are the workspace's cache and must survive the checkout.");
    }

    [Test]
    public void PrepareWorkspace_PrunesDeletedTag()
    {
        InitRemote();
        var c1 = WriteAndCommit(_remoteDir, "file.txt", "v1");
        TagAt(_remoteDir, "obsolete", c1);

        WorkspaceGitHelper.PrepareWorkspace(_workspaceDir, _remoteDir, c1);
        Assert.That(LookupTag(_workspaceDir, "obsolete"), Is.EqualTo(c1));

        DeleteTag(_remoteDir, "obsolete");

        WorkspaceGitHelper.PrepareWorkspace(_workspaceDir, _remoteDir, c1);

        Assert.That(LookupTag(_workspaceDir, "obsolete"), Is.Null,
            "Stale tag should be pruned when removed on the remote.");
    }

    private void InitRemote()
    {
        InitRepo(_remoteDir);
    }
}
