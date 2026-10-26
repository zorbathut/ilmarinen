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
public class GitMirrorTests
{
    private string _root = null!;
    private string _remoteDir = null!;
    private string _workspaceRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mirror-test-{Guid.NewGuid():N}");
        _remoteDir = Path.Combine(_root, "remote");
        _workspaceRoot = Path.Combine(_root, "workspaces");
        Directory.CreateDirectory(_workspaceRoot);
        InitRepo(_remoteDir);
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
    public void Sync_PicksUpCommitsMadeSinceTheLastSync()
    {
        WriteAndCommit(_remoteDir, "file.txt", "v1");
        Sync();
        var c2 = WriteAndCommit(_remoteDir, "file.txt", "v2");

        Sync();

        Assert.That(ResolveSha("master"), Is.EqualTo(c2));
    }

    [Test]
    public void Sync_FollowsForcedMovesAndDeletionsOfBranchesAndTags()
    {
        var c1 = WriteAndCommit(_remoteDir, "file.txt", "v1");
        CreateBranch(_remoteDir, "feature", c1);
        CreateBranch(_remoteDir, "doomed", c1);
        TagAt(_remoteDir, "v1", c1);
        TagAt(_remoteDir, "obsolete", c1);
        Sync();

        var c2 = WriteAndCommit(_remoteDir, "other.txt", "x");
        UpdateBranch(_remoteDir, "feature", c2);
        DeleteBranch(_remoteDir, "doomed");
        DeleteTag(_remoteDir, "v1");
        TagAt(_remoteDir, "v1", c2);
        DeleteTag(_remoteDir, "obsolete");
        Sync();

        using var mirror = new Repository(MirrorPath());
        Assert.That(mirror.Branches["feature"].Tip.Sha, Is.EqualTo(c2));
        Assert.That(mirror.Branches["doomed"], Is.Null);
        Assert.That(mirror.Tags["v1"].Target.Sha, Is.EqualTo(c2));
        Assert.That(mirror.Tags["obsolete"], Is.Null);
    }

    [Test]
    public void Sync_KeepsOnlyTheRemotesBranchesAndTags()
    {
        var c1 = WriteAndCommit(_remoteDir, "file.txt", "v1");
        CreateBranch(_remoteDir, "feature", c1);
        TagAt(_remoteDir, "v1", c1);

        Sync();
        Sync();

        using var mirror = new Repository(MirrorPath());
        Assert.That(mirror.Refs.Select(r => r.CanonicalName), Is.EquivalentTo(new[] { "refs/heads/master", "refs/heads/feature", "refs/tags/v1" }));
    }

    [Test]
    public void Sync_MirrorItCannotOpen_IsRebuiltWhenTheRemoteAnswers()
    {
        var c1 = WriteAndCommit(_remoteDir, "file.txt", "v1");
        Sync();
        File.WriteAllText(Path.Combine(MirrorPath(), "HEAD"), "garbage");

        Sync();

        Assert.That(ResolveSha("master"), Is.EqualTo(c1));
    }

    [Test]
    public void Sync_MirrorWithTooManyPacks_IsRebuilt()
    {
        var c1 = WriteAndCommit(_remoteDir, "file.txt", "v1");
        Sync();
        var packDir = Path.Combine(MirrorPath(), "objects", "pack");
        for (var i = 0; i < 65; i++)
        {
            File.WriteAllText(Path.Combine(packDir, $"pack-fake{i}.pack"), "");
        }

        Sync();

        Assert.That(Directory.GetFiles(packDir, "*.pack"), Has.Length.LessThan(65));
        Assert.That(ResolveSha("master"), Is.EqualTo(c1));
    }

    /// <summary>A lost pack leaves the refs and newest objects readable, so the damage shows up only when an older object is read — which must count as damage, not as something missing from the repository.</summary>
    [Test]
    public void IsDamage_ReadingAnObjectFromALostPack()
    {
        WriteAndCommit(_remoteDir, "script.csx", "old content");
        Sync();
        var packDir = Path.Combine(MirrorPath(), "objects", "pack");
        var firstPack = Directory.GetFiles(packDir, "*.pack").Single();
        WriteAndCommit(_remoteDir, "other.txt", "new content");
        Sync();
        File.Delete(firstPack);
        File.Delete(Path.ChangeExtension(firstPack, ".idx"));

        using var mirror = new Repository(MirrorPath());
        var commit = GitMirror.Resolve(mirror, "master");
        var ex = Assert.Catch(() => ((Blob)commit.Tree["script.csx"].Target).GetContentText());
        Assert.That(GitMirror.IsDamage(ex!), Is.True, ex!.ToString());
    }

    [Test]
    public void IsDamage_NotForAMalformedRefName()
    {
        WriteAndCommit(_remoteDir, "file.txt", "v1");
        Sync();

        using var mirror = new Repository(MirrorPath());
        var ex = Assert.Catch(() => GitMirror.Resolve(mirror, "bad..ref^{nope"));
        Assert.That(GitMirror.IsDamage(ex!), Is.False, ex!.ToString());
    }

    [Test]
    public void CloneWorkspace_HasTheRemotesDefaultBranchAndOriginHead()
    {
        var c1 = WriteAndCommit(_remoteDir, "file.txt", "v1");
        CreateBranch(_remoteDir, "main", c1);
        using (var remote = new Repository(_remoteDir))
        {
            remote.Refs.UpdateTarget("HEAD", "refs/heads/main");
        }
        Sync();
        var workDir = Path.Combine(_workspaceRoot, "job");

        WorkspaceGitHelper.CloneWorkspace(MirrorPath(), workDir, _remoteDir, c1);

        using var repo = new Repository(workDir);
        Assert.That(repo.Refs["refs/remotes/origin/HEAD"]?.TargetIdentifier, Is.EqualTo("refs/remotes/origin/main"));
        Assert.That(repo.Branches["main"], Is.Not.Null);
    }

    [TestCase("feature")]
    [TestCase("origin/feature")]
    [TestCase("refs/remotes/origin/feature")]
    [TestCase("refs/heads/feature")]
    public void Resolve_AcceptsTheSpellingsAWorkspaceCloneAccepts(string gitRef)
    {
        WriteAndCommit(_remoteDir, "file.txt", "v1");
        var feature = WriteAndCommit(_remoteDir, "file.txt", "v2");
        CreateBranch(_remoteDir, "feature", feature);
        UpdateBranch(_remoteDir, "master", LookupParent(_remoteDir, feature));
        Sync();

        Assert.That(ResolveSha(gitRef), Is.EqualTo(feature));
    }

    [Test]
    public void Resolve_ByShaAndTag()
    {
        var c1 = WriteAndCommit(_remoteDir, "file.txt", "v1");
        TagAt(_remoteDir, "v1", c1);
        WriteAndCommit(_remoteDir, "file.txt", "v2");
        Sync();

        Assert.That(ResolveSha(c1), Is.EqualTo(c1));
        Assert.That(ResolveSha(c1[..10]), Is.EqualTo(c1));
        Assert.That(ResolveSha("v1"), Is.EqualTo(c1));
    }

    [Test]
    public void Resolve_MissingRef_Throws()
    {
        WriteAndCommit(_remoteDir, "file.txt", "v1");
        Sync();

        using var mirror = new Repository(MirrorPath());
        Assert.Throws<InvalidOperationException>(() => GitMirror.Resolve(mirror, "no-such-branch"));
    }

    /// <summary>Unreadable objects are damage to repair by rebuilding the mirror, not a ref to report as missing.</summary>
    [Test]
    public void Resolve_RefWhoseCommitIsUnreadable_ThrowsAGitError()
    {
        WriteAndCommit(_remoteDir, "file.txt", "v1");
        Sync();
        foreach (var file in Directory.GetFiles(Path.Combine(MirrorPath(), "objects"), "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.WriteAllText(file, "garbage");
        }

        using var mirror = new Repository(MirrorPath());
        Assert.Throws<LibGit2SharpException>(() => GitMirror.Resolve(mirror, "master"));
    }

    /// <summary>The mirror keeps a force-pushed-away commit's objects, but a persistent workspace fetching from the mirror only receives what its refs reach, so such a commit is refused up front.</summary>
    [Test]
    public void Resolve_ShaNoRefReaches_Throws()
    {
        var c1 = WriteAndCommit(_remoteDir, "file.txt", "v1");
        var orphan = WriteAndCommit(_remoteDir, "file.txt", "v2");
        Sync();
        UpdateBranch(_remoteDir, "master", c1);
        Sync();

        using var mirror = new Repository(MirrorPath());
        Assert.That(mirror.Lookup<Commit>(orphan), Is.Not.Null, "precondition: the mirror still has the object");
        var ex = Assert.Throws<InvalidOperationException>(() => GitMirror.Resolve(mirror, orphan));
        Assert.That(ex!.Message, Does.Contain(orphan));
    }

    [Test]
    public void Sync_UnreachableRemote_KeepsTheMirrorAndThrows()
    {
        WriteAndCommit(_remoteDir, "file.txt", "v1");
        Sync();
        var moved = _remoteDir + "-gone";
        Directory.Move(_remoteDir, moved);

        Assert.Catch<LibGit2SharpException>(() => Sync());
        Assert.That(Directory.Exists(MirrorPath()), Is.True);

        Assert.Catch<LibGit2SharpException>(() => GitMirror.Rebuild(_workspaceRoot, _remoteDir, null));
        Assert.That(Directory.Exists(MirrorPath()), Is.True, "a failed rebuild leaves the old mirror in place");
        Assert.That(Directory.GetDirectories(Path.GetDirectoryName(MirrorPath())!), Has.Length.EqualTo(1), "and nothing half-built beside it");

        Directory.Move(moved, _remoteDir);
        Assert.That(ResolveSha("master"), Is.Not.Empty);
    }

    [Test]
    public void Rebuild_ReplacesADamagedMirror()
    {
        var c1 = WriteAndCommit(_remoteDir, "file.txt", "v1");
        Sync();
        foreach (var file in Directory.GetFiles(Path.Combine(MirrorPath(), "objects"), "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.WriteAllText(file, "garbage");
        }

        GitMirror.Rebuild(_workspaceRoot, _remoteDir, null);

        using var mirror = new Repository(MirrorPath());
        Assert.That(mirror.Lookup<Commit>(c1)?.Tree["file.txt"], Is.Not.Null);
        Assert.That(Directory.GetDirectories(Path.GetDirectoryName(MirrorPath())!), Has.Length.EqualTo(1), "the old mirror is cleaned away");
    }

    [Test]
    public void Rebuild_ClearsLeftoversOfAnInterruptedRebuild()
    {
        WriteAndCommit(_remoteDir, "file.txt", "v1");
        Sync();
        Directory.CreateDirectory(MirrorPath() + ".tmp-0123");
        Directory.CreateDirectory(MirrorPath() + ".old-4567");

        GitMirror.Rebuild(_workspaceRoot, _remoteDir, null);

        Assert.That(Directory.GetDirectories(Path.GetDirectoryName(MirrorPath())!), Is.EqualTo(new[] { MirrorPath() }));
    }

    /// <summary>The checkout is what the steps see at /workspace, where git must behave as in an ordinary clone of the real repository.</summary>
    [Test]
    public void CloneWorkspace_LooksLikeAnOrdinaryCloneOfTheRealRepository()
    {
        var c1 = WriteAndCommit(_remoteDir, "file.txt", "v1");
        TagAt(_remoteDir, "v1", c1);
        CreateBranch(_remoteDir, "feature", c1);
        var c2 = WriteAndCommit(_remoteDir, "file.txt", "v2");
        Sync();
        var workDir = Path.Combine(_workspaceRoot, "job");

        WorkspaceGitHelper.CloneWorkspace(MirrorPath(), workDir, "https://example.invalid/repo.git", c1);

        using var repo = new Repository(workDir);
        Assert.That(repo.Network.Remotes["origin"].Url, Is.EqualTo("https://example.invalid/repo.git"));
        Assert.That(repo.Head.Tip.Sha, Is.EqualTo(c1));
        Assert.That(repo.Branches["origin/master"]?.Tip.Sha, Is.EqualTo(c2));
        Assert.That(repo.Branches["origin/feature"]?.Tip.Sha, Is.EqualTo(c1));
        Assert.That(repo.Describe(repo.Head.Tip, new DescribeOptions { Strategy = DescribeStrategy.Tags }), Is.EqualTo("v1"));
        Assert.That(repo.RetrieveStatus().IsDirty, Is.False);
        Assert.That(File.ReadAllText(Path.Combine(workDir, "file.txt")), Is.EqualTo("v1"));
    }

    /// <summary>A mirror's HEAD names whatever branch it was created with, which the remote may since have deleted.</summary>
    [Test]
    public void CloneWorkspace_FromAMirrorWhoseHeadDangles()
    {
        var c1 = WriteAndCommit(_remoteDir, "file.txt", "v1");
        CreateBranch(_remoteDir, "main", c1);
        Sync();
        using (var remote = new Repository(_remoteDir))
        {
            remote.Refs.UpdateTarget("HEAD", "refs/heads/main");
        }
        DeleteBranch(_remoteDir, "master");
        Sync();
        var workDir = Path.Combine(_workspaceRoot, "job");

        WorkspaceGitHelper.CloneWorkspace(MirrorPath(), workDir, _remoteDir, c1);

        using var repo = new Repository(workDir);
        Assert.That(repo.Head.Tip.Sha, Is.EqualTo(c1));
        Assert.That(repo.RetrieveStatus().IsDirty, Is.False);
    }

    [TestCase("https://github.com/Org/Repo.git", "https://GITHUB.com/Org/Repo")]
    [TestCase("https://github.com/Org/Repo", "https://x-access-token:secret@github.com/Org/Repo/")]
    [TestCase("git@github.com:Org/Repo.git", "ssh://git@github.com/Org/Repo")]
    [TestCase("git@github.com:Org/Repo.git", "https://github.com/Org/Repo")]
    public void RepositoryKey_SameRepository(string a, string b)
    {
        Assert.That(GitMirror.RepositoryKey(a), Is.EqualTo(GitMirror.RepositoryKey(b)));
    }

    [TestCase("https://github.com/Org/Repo", "https://github.com/org/repo")]
    [TestCase("https://github.com/Org/Repo", "https://gitlab.com/Org/Repo")]
    public void RepositoryKey_DifferentRepositories(string a, string b)
    {
        Assert.That(GitMirror.RepositoryKey(a), Is.Not.EqualTo(GitMirror.RepositoryKey(b)));
    }

    private void Sync()
    {
        GitMirror.Sync(_workspaceRoot, _remoteDir, null, NullLogger.Instance);
    }

    private string MirrorPath()
    {
        return GitMirror.PathFor(_workspaceRoot, _remoteDir);
    }

    private string ResolveSha(string gitRef)
    {
        using var mirror = new Repository(GitMirror.Sync(_workspaceRoot, _remoteDir, null, NullLogger.Instance));
        return GitMirror.Resolve(mirror, gitRef).Sha;
    }

    private static string LookupParent(string repoPath, string sha)
    {
        using var repo = new Repository(repoPath);
        return repo.Lookup<Commit>(sha).Parents.Single().Sha;
    }
}
