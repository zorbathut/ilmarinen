using LibGit2Sharp;
using System.IO;
using System;

namespace Ilmarinen.Worker.Services;

/// <summary>
/// Handles git operations on job workspaces.
/// </summary>
public static class WorkspaceGitHelper
{
    private const string SourceRemote = "ilmarinen-source";

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
    /// Updates a persistent workspace's remote-tracking branches and tags from <paramref name="source"/> (a local mirror), leaving <c>origin</c> naming the real repository.
    /// </summary>
    public static void FetchIntoWorkspace(string path, string source)
    {
        using var repo = new Repository(path);

        // The persistent workspace is a cache; the remote is authoritative. Force-fetch all branches and tags (the leading `+` overwrites local refs on non-fast-forward updates), and prune anything deleted on the remote.
        var refSpecs = new[]
        {
            "+refs/heads/*:refs/remotes/origin/*",
            "+refs/tags/*:refs/tags/*",
        };
        var fetchOptions = new FetchOptions
        {
            Prune = true,
            // libgit2 only honours an explicit `+refs/tags/*:refs/tags/*` refspec when TagFetchMode is also set; without this, force-overwriting a diverged local tag silently no-ops.
            TagFetchMode = TagFetchMode.All,
        };

        // Removing the remote also deletes the refs its configured refspec maps to, so that must stay its own refs/remotes/ilmarinen-source rather than origin's
        if (repo.Network.Remotes[SourceRemote] != null)
        {
            repo.Network.Remotes.Remove(SourceRemote);
        }

        repo.Network.Remotes.Add(SourceRemote, source);
        try
        {
            Commands.Fetch(repo, SourceRemote, refSpecs, fetchOptions, null);
        }
        finally
        {
            repo.Network.Remotes.Remove(SourceRemote);
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
