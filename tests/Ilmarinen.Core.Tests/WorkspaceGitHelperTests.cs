using Ilmarinen.Worker.Services;
using LibGit2Sharp;
using NUnit.Framework;
using System;
using System.IO;

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
        Repository.Init(_remoteDir);
        using var repo = new Repository(_remoteDir);
        repo.Refs.UpdateTarget("HEAD", "refs/heads/master");
    }

    private static string WriteAndCommit(string repoPath, string relPath, string content)
    {
        var fullPath = Path.Combine(repoPath, relPath);
        File.WriteAllText(fullPath, content);
        using var repo = new Repository(repoPath);
        Commands.Stage(repo, relPath);
        var sig = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
        var commit = repo.Commit($"set {relPath}={content}", sig, sig);
        return commit.Sha;
    }

    private static void TagAt(string repoPath, string tagName, string sha)
    {
        using var repo = new Repository(repoPath);
        repo.ApplyTag(tagName, sha);
    }

    private static void DeleteTag(string repoPath, string tagName)
    {
        using var repo = new Repository(repoPath);
        repo.Tags.Remove(tagName);
    }

    private static void CreateBranch(string repoPath, string branchName, string sha)
    {
        using var repo = new Repository(repoPath);
        var commit = repo.Lookup<Commit>(sha);
        repo.CreateBranch(branchName, commit);
    }

    private static void UpdateBranch(string repoPath, string branchName, string sha)
    {
        using var repo = new Repository(repoPath);
        repo.Refs.UpdateTarget($"refs/heads/{branchName}", sha);
    }

    private static void DeleteBranch(string repoPath, string branchName)
    {
        using var repo = new Repository(repoPath);
        repo.Branches.Remove(branchName);
    }

    private static string? LookupTag(string repoPath, string tagName)
    {
        using var repo = new Repository(repoPath);
        var tag = repo.Tags[tagName];
        return tag?.Target.Sha;
    }

    private static string? LookupRemoteBranch(string repoPath, string branchName)
    {
        using var repo = new Repository(repoPath);
        var branch = repo.Branches[$"origin/{branchName}"];
        return branch?.Tip?.Sha;
    }

    private static void ForceDeleteDirectory(string path)
    {
        var dir = new DirectoryInfo(path);
        foreach (var sub in dir.GetDirectories("*", SearchOption.AllDirectories))
        {
            foreach (var file in sub.GetFiles())
            {
                file.Attributes = FileAttributes.Normal;
            }
        }
        foreach (var file in dir.GetFiles("*", SearchOption.AllDirectories))
        {
            file.Attributes = FileAttributes.Normal;
        }
        Directory.Delete(path, recursive: true);
    }
}
