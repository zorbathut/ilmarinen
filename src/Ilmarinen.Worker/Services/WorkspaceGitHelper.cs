using LibGit2Sharp;
using System.IO;
using System;

namespace Ilmarinen.Worker.Services;

/// <summary>
/// Handles git operations on job workspaces.
/// </summary>
public static class WorkspaceGitHelper
{
    /// <summary>
    /// Clones <paramref name="source"/> (a local mirror) into <paramref name="path"/> and checks out <paramref name="commitSha"/>, leaving a self-contained repository whose origin is <paramref name="repoUrl"/>.
    /// </summary>
    public static void CloneWorkspace(string source, string path, string repoUrl, string commitSha)
    {
        // A local clone hardlinks the source's object files, which is what makes it cheap. Never reach for alternates or a shared clone instead: the steps see this directory through a /workspace bind mount that the mirror isn't part of, and git inside the container would find no objects.
        Repository.Clone(source, path, new CloneOptions { Checkout = false });

        using (var repo = new Repository(path))
        {
            repo.Network.Remotes.Update("origin", remote => remote.Url = repoUrl);
        }

        CheckoutCommit(path, commitSha);
    }

    /// <summary>
    /// Readies an existing persistent workspace for an update: checks it is a clone of <paramref name="repoUrl"/> and discards what the previous run changed in tracked files.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the workspace has no origin or its origin isn't <paramref name="repoUrl"/>.
    /// </exception>
    public static void ResetWorkspace(string path, string repoUrl)
    {
        using var repo = new Repository(path);

        // Validate repo URL matches
        var origin = repo.Network.Remotes["origin"];
        if (origin == null)
        {
            throw new InvalidOperationException(
                $"Workspace at '{path}' has no 'origin' remote configured.");
        }

        if (!UrlsMatch(origin.Url, repoUrl))
        {
            throw new InvalidOperationException(
                $"Workspace repo mismatch: workspace has '{origin.Url}', job wants '{repoUrl}'. " +
                $"Delete the workspace directory to resolve.");
        }

        // Reset any modified/staged files from previous runs
        repo.Reset(ResetMode.Hard);
    }

    /// <summary>
    /// Updates a persistent workspace's remote-tracking branches and tags from <paramref name="mirrorPath"/>, leaving <c>origin</c> naming the real repository.
    /// </summary>
    /// <exception cref="MirrorDamagedException">Thrown if the fetch fails and the mirror fails its own check too.</exception>
    /// <exception cref="GitCommandException">Thrown if the fetch fails on the workspace's own state.</exception>
    public static void FetchIntoWorkspace(string path, string mirrorPath)
    {
        // This is git's own fetch, not libgit2's: libgit2 fetching from a local path packs everything the wanted refs reach regardless of what the workspace already has, which on a large repository costs as much as a clone, where git negotiates and transfers only what's missing.

        // The persistent workspace is a cache; the remote is authoritative. Force-fetch all branches and tags (the leading `+` overwrites local refs on non-fast-forward updates), and prune anything deleted on the remote. Submodules a step checked out are the step's business, and maintenance would repack the whole repository during the job.
        try
        {
            GitCli.Run(path, "fetch", "--quiet", "--prune", "--no-recurse-submodules", "--no-auto-maintenance", "--no-write-fetch-head", mirrorPath, "+refs/heads/*:refs/remotes/origin/*", "+refs/tags/*:refs/tags/*");
        }
        catch (GitCommandException ex)
        {
            // A fetch also fails on the workspace's own state, such as a ref lock a killed step left behind, which a new mirror wouldn't fix
            GitMirror.Verify(mirrorPath, ex);
            throw;
        }
    }

    /// <summary>
    /// Checks out <paramref name="commitSha"/> as a detached HEAD.
    /// </summary>
    public static void CheckoutCommit(string path, string commitSha)
    {
        using var repo = new Repository(path);

        var commit = repo.Lookup<Commit>(commitSha)
            ?? throw new InvalidOperationException($"Commit '{commitSha}' is not in the repository at '{path}'.");

        // The target commit is authoritative (matching FetchIntoWorkspace's force-fetch): force checkout so untracked leftovers from previous runs — e.g. build-generated sidecar files a newer commit now tracks — are overwritten instead of failing the job with a checkout conflict.
        Commands.Checkout(repo, commit, new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
    }

    private static bool UrlsMatch(string a, string b)
    {
        return NormalizeUrl(a) == NormalizeUrl(b);
    }

    private static string NormalizeUrl(string url)
    {
        // Normalize git URLs for comparison:
        // - Remove trailing .git
        // - Lowercase
        // - Handle http vs https (treat as same)
        // - Handle git@ vs https:// (treat as same host)

        url = url.Trim().ToLowerInvariant();

        // Remove trailing .git
        if (url.EndsWith(".git"))
        {
            url = url[..^4];
        }

        // Remove trailing slash
        url = url.TrimEnd('/');

        // Convert git@host:path to https://host/path for comparison
        if (url.StartsWith("git@"))
        {
            // git@github.com:org/repo -> github.com/org/repo
            url = url[4..].Replace(":", "/");
        }
        else if (url.StartsWith("https://"))
        {
            url = url[8..];
        }
        else if (url.StartsWith("http://"))
        {
            url = url[7..];
        }

        return url;
    }
}
