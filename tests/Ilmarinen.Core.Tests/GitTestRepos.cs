using LibGit2Sharp;
using System.IO;
using System;

namespace Ilmarinen.Core.Tests;

/// <summary>
/// Builds and inspects the throwaway git repositories the workspace and mirror tests run against.
/// </summary>
public static class GitTestRepos
{
    /// <summary>A repository with HEAD on master and no commits.</summary>
    public static void InitRepo(string path)
    {
        Repository.Init(path);
        using var repo = new Repository(path);
        repo.Refs.UpdateTarget("HEAD", "refs/heads/master");
    }

    public static string WriteAndCommit(string repoPath, string relPath, string content)
    {
        var fullPath = Path.Combine(repoPath, relPath);
        File.WriteAllText(fullPath, content);
        using var repo = new Repository(repoPath);
        Commands.Stage(repo, relPath);
        var sig = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
        var commit = repo.Commit($"set {relPath}={content}", sig, sig);
        return commit.Sha;
    }

    public static void TagAt(string repoPath, string tagName, string sha)
    {
        using var repo = new Repository(repoPath);
        repo.ApplyTag(tagName, sha);
    }

    public static void DeleteTag(string repoPath, string tagName)
    {
        using var repo = new Repository(repoPath);
        repo.Tags.Remove(tagName);
    }

    public static void CreateBranch(string repoPath, string branchName, string sha)
    {
        using var repo = new Repository(repoPath);
        var commit = repo.Lookup<Commit>(sha);
        repo.CreateBranch(branchName, commit);
    }

    public static void UpdateBranch(string repoPath, string branchName, string sha)
    {
        using var repo = new Repository(repoPath);
        repo.Refs.UpdateTarget($"refs/heads/{branchName}", sha);
    }

    public static void DeleteBranch(string repoPath, string branchName)
    {
        using var repo = new Repository(repoPath);
        repo.Branches.Remove(branchName);
    }

    public static string? LookupTag(string repoPath, string tagName)
    {
        using var repo = new Repository(repoPath);
        var tag = repo.Tags[tagName];
        return tag?.Target.Sha;
    }

    public static string? LookupRemoteBranch(string repoPath, string branchName)
    {
        using var repo = new Repository(repoPath);
        var branch = repo.Branches[$"origin/{branchName}"];
        return branch?.Tip?.Sha;
    }

    public static void ForceDeleteDirectory(string path)
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
