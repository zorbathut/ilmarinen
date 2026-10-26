using Ilmarinen.Worker.Services;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using System.IO;
using System.Linq;
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
    public void UpdatedWorkspace_ForceUpdatesDivergedTag()
    {
        // Remote: c1 tagged v1.0
        InitRemote();
        var c1 = WriteAndCommit(_remoteDir, "file.txt", "v1");
        TagAt(_remoteDir, "v1.0", c1);

        // Initial clone of workspace
        Prepare(c1);
        Assert.That(LookupTag(_workspaceDir, "v1.0"), Is.EqualTo(c1));

        // Remote moves v1.0 to a new commit c2 (forced retag)
        var c2 = WriteAndCommit(_remoteDir, "file.txt", "v2");
        DeleteTag(_remoteDir, "v1.0");
        TagAt(_remoteDir, "v1.0", c2);

        // Workspace runs again, pinned to c2
        Prepare(c2);

        Assert.That(LookupTag(_workspaceDir, "v1.0"), Is.EqualTo(c2),
            "Tag v1.0 should be force-updated to the new commit on the remote.");
    }

    [Test]
    public void UpdatedWorkspace_ForceUpdatesDivergedBranch()
    {
        // Remote: master at c1, plus branch 'feature' at c1
        InitRemote();
        var c1 = WriteAndCommit(_remoteDir, "file.txt", "v1");
        CreateBranch(_remoteDir, "feature", c1);

        Prepare(c1);
        Assert.That(LookupRemoteBranch(_workspaceDir, "feature"), Is.EqualTo(c1));

        // Remote rewrites 'feature' to point at a new commit c2 (non-fast-forward)
        var c2 = WriteAndCommit(_remoteDir, "other.txt", "x");
        UpdateBranch(_remoteDir, "feature", c2);

        // Run again pinned to master tip (c2 is master tip after second commit)
        Prepare(c2);

        Assert.That(LookupRemoteBranch(_workspaceDir, "feature"), Is.EqualTo(c2),
            "Remote-tracking branch origin/feature should be force-updated.");
    }

    [Test]
    public void UpdatedWorkspace_PrunesDeletedBranch()
    {
        InitRemote();
        var c1 = WriteAndCommit(_remoteDir, "file.txt", "v1");
        CreateBranch(_remoteDir, "feature", c1);

        Prepare(c1);
        Assert.That(LookupRemoteBranch(_workspaceDir, "feature"), Is.EqualTo(c1));

        // Remote deletes 'feature'
        DeleteBranch(_remoteDir, "feature");

        Prepare(c1);

        Assert.That(LookupRemoteBranch(_workspaceDir, "feature"), Is.Null,
            "Stale remote-tracking branch should be pruned.");
    }

    [Test]
    public void UpdatedWorkspace_OverwritesUntrackedFileCollidingWithNewCommit()
    {
        InitRemote();
        var c1 = WriteAndCommit(_remoteDir, "file.txt", "v1");
        Prepare(c1);

        // A previous run left untracked files (e.g. build-generated sidecars); the remote then adds a tracked file at one of those paths
        File.WriteAllText(Path.Combine(_workspaceDir, "generated.txt"), "stale local content");
        File.WriteAllText(Path.Combine(_workspaceDir, "cache.txt"), "build cache");
        var c2 = WriteAndCommit(_remoteDir, "generated.txt", "tracked content");

        Prepare(c2);

        Assert.That(File.ReadAllText(Path.Combine(_workspaceDir, "generated.txt")), Is.EqualTo("tracked content"),
            "Checkout should overwrite the untracked file with the tracked version from the target commit.");
        Assert.That(File.ReadAllText(Path.Combine(_workspaceDir, "cache.txt")), Is.EqualTo("build cache"),
            "Non-colliding untracked files are the workspace's cache and must survive the checkout.");
    }

    [Test]
    public void UpdatedWorkspace_LeavesOriginAndNoOtherRemote()
    {
        InitRemote();
        var c1 = WriteAndCommit(_remoteDir, "file.txt", "v1");
        Prepare(c1);
        var c2 = WriteAndCommit(_remoteDir, "file.txt", "v2");

        Prepare(c2);

        using var repo = new Repository(_workspaceDir);
        Assert.That(repo.Network.Remotes.Select(r => r.Name), Is.EqualTo(new[] { "origin" }));
        Assert.That(repo.Refs.Select(r => r.CanonicalName).Where(n => n.StartsWith("refs/remotes/") && !n.StartsWith("refs/remotes/origin/")), Is.Empty);
        Assert.That(LookupRemoteBranch(_workspaceDir, "master"), Is.EqualTo(c2));
    }

    [Test]
    public void UpdatedWorkspace_PrunesDeletedTag()
    {
        InitRemote();
        var c1 = WriteAndCommit(_remoteDir, "file.txt", "v1");
        TagAt(_remoteDir, "obsolete", c1);

        Prepare(c1);
        Assert.That(LookupTag(_workspaceDir, "obsolete"), Is.EqualTo(c1));

        DeleteTag(_remoteDir, "obsolete");

        Prepare(c1);

        Assert.That(LookupTag(_workspaceDir, "obsolete"), Is.Null,
            "Stale tag should be pruned when removed on the remote.");
    }

    [Test]
    public void UpdatedWorkspace_MissingMirror_IsMirrorDamage()
    {
        InitRemote();
        var c1 = WriteAndCommit(_remoteDir, "file.txt", "v1");
        Prepare(c1);

        var ex = Assert.Throws<MirrorDamagedException>(() => WorkspaceGitHelper.FetchIntoWorkspace(_workspaceDir, Path.Combine(_root, "no-such-mirror")));

        Assert.That(GitMirror.IsDamage(ex!), Is.True);
    }

    /// <summary>A step killed mid-git leaves its lock behind; that's the workspace's problem, and rebuilding the mirror for it would only cost a full clone and fail again.</summary>
    [Test]
    public void UpdatedWorkspace_StaleRefLock_IsNotMirrorDamage()
    {
        InitRemote();
        var c1 = WriteAndCommit(_remoteDir, "file.txt", "v1");
        Prepare(c1);
        File.WriteAllText(Path.Combine(_workspaceDir, ".git", "refs", "remotes", "origin", "master.lock"), "");
        var c2 = WriteAndCommit(_remoteDir, "file.txt", "v2");

        var ex = Assert.Catch(() => Prepare(c2));

        Assert.That(ex, Is.TypeOf<GitCommandException>());
        Assert.That(GitMirror.IsDamage(ex!), Is.False);
    }

    /// <summary>Hooks in the workspace were written by a step, and must not run inside the worker.</summary>
    [Test]
    public void UpdatedWorkspace_DoesNotRunHooksAStepPlanted()
    {
        Assume.That(OperatingSystem.IsWindows(), Is.False, "hooks need an executable shell script");
        InitRemote();
        var c1 = WriteAndCommit(_remoteDir, "file.txt", "v1");
        Prepare(c1);
        var marker = Path.Combine(_root, "hook-ran");
        var hook = Path.Combine(_workspaceDir, ".git", "hooks", "reference-transaction");
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        File.WriteAllText(hook, $"#!/bin/sh\ntouch '{marker}'\n");
        File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var c2 = WriteAndCommit(_remoteDir, "file.txt", "v2");

        Prepare(c2);

        Assert.That(File.Exists(marker), Is.False);
        Assert.That(File.ReadAllText(Path.Combine(_workspaceDir, "file.txt")), Is.EqualTo("v2"));
    }

    /// <summary>libgit2 fetching from a local path re-sends everything the refs reach, which on a large repository costs as much as a clone; an update must carry only what's new.</summary>
    [Test]
    public void UpdatedWorkspace_TransfersOnlyWhatIsNew()
    {
        InitRemote();
        for (var i = 0; i < 150; i++)
        {
            File.WriteAllText(Path.Combine(_remoteDir, $"file{i}.txt"), $"content {i}");
        }
        var c1 = CommitAll(_remoteDir);
        Prepare(c1);
        var before = ObjectFiles(_workspaceDir);
        var c2 = WriteAndCommit(_remoteDir, "file0.txt", "changed");

        Prepare(c2);

        var added = ObjectFiles(_workspaceDir).Except(before).ToList();
        Assert.That(added.Where(f => f.EndsWith(".pack")), Is.Empty, string.Join("\n", added));
        Assert.That(added, Has.Count.LessThanOrEqualTo(3), "the new commit, its tree and its blob");
    }

    /// <summary>The first run of a workspace clones it from the mirror and later runs update it from there, as a job does.</summary>
    private void Prepare(string sha)
    {
        var mirror = GitMirror.Sync(Path.Combine(_root, "workspaces"), _remoteDir, null, NullLogger.Instance);
        if (Directory.Exists(_workspaceDir))
        {
            WorkspaceGitHelper.ResetWorkspace(_workspaceDir, _remoteDir);
            WorkspaceGitHelper.FetchIntoWorkspace(_workspaceDir, mirror);
            WorkspaceGitHelper.CheckoutCommit(_workspaceDir, sha);
        }
        else
        {
            WorkspaceGitHelper.CloneWorkspace(mirror, _workspaceDir, _remoteDir, sha);
        }
    }

    private static string CommitAll(string repoPath)
    {
        using var repo = new Repository(repoPath);
        Commands.Stage(repo, "*");
        var sig = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
        return repo.Commit("add everything", sig, sig).Sha;
    }

    private static string[] ObjectFiles(string workDir)
    {
        var objects = Path.Combine(workDir, ".git", "objects");
        return Directory.GetFiles(objects, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(objects, f)).ToArray();
    }

    private void InitRemote()
    {
        InitRepo(_remoteDir);
    }
}
