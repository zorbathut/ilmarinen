using LibGit2Sharp;
using System;
using System.IO;

namespace Ilmarinen.Worker.Services;

/// <summary>
/// Handles git operations for persistent workspaces.
/// </summary>
public static class WorkspaceGitHelper
{
    /// <summary>
    /// Prepare a persistent workspace by cloning or updating the repository.
    /// </summary>
    /// <param name="path">Workspace directory path</param>
    /// <param name="repoUrl">Expected repository URL</param>
    /// <param name="gitRef">Git ref to checkout (branch, tag, or commit)</param>
    /// <param name="gitToken">Optional Git token for HTTPS authentication</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown if workspace exists but is not a git repo, repo URL doesn't match, or workspace has uncommitted changes.
    /// </exception>
    public static void PrepareWorkspace(string path, string repoUrl, string gitRef, string? gitToken = null)
    {
        Directory.CreateDirectory(path);
        var gitDir = Path.Combine(path, ".git");

        if (!Directory.Exists(gitDir))
        {
            // Empty workspace - clone
            var cloneOptions = new CloneOptions();
            if (!string.IsNullOrEmpty(gitToken))
            {
                cloneOptions.FetchOptions.CredentialsProvider = (url, user, types) =>
                    new UsernamePasswordCredentials
                    {
                        Username = "git",
                        Password = gitToken
                    };
            }
            Repository.Clone(repoUrl, path, cloneOptions);
        }
        else
        {
            // Existing workspace - validate and update
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

            // The persistent workspace is a cache; the remote is authoritative. Force-fetch all branches and tags (the leading `+` overwrites local refs on non-fast-forward updates), and prune anything deleted on the remote.
            var remote = repo.Network.Remotes["origin"];
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
            if (!string.IsNullOrEmpty(gitToken))
            {
                fetchOptions.CredentialsProvider = (url, user, types) =>
                    new UsernamePasswordCredentials
                    {
                        Username = "git",
                        Password = gitToken
                    };
            }
            Commands.Fetch(repo, remote.Name, refSpecs, fetchOptions, null);
        }

        // Checkout the ref
        CheckoutRef(path, gitRef);
    }

    /// <summary>
    /// Checks out the given ref and returns the resolved commit SHA.
    /// </summary>
    internal static string CheckoutRef(string path, string gitRef)
    {
        using var repo = new Repository(path);

        // Try to find the ref as a direct object (tag, commit SHA)
        var target = repo.Lookup(gitRef);
        if (target != null)
        {
            var commit = target as Commit ?? target.Peel<Commit>();
            Commands.Checkout(repo, commit);
            return repo.Head.Tip.Sha;
        }

        // Try as a remote branch (origin/xxx)
        var remoteBranch = repo.Branches[$"origin/{gitRef}"];
        if (remoteBranch != null)
        {
            Commands.Checkout(repo, remoteBranch.Tip);
            return repo.Head.Tip.Sha;
        }

        // Try as a local branch
        var localBranch = repo.Branches[gitRef];
        if (localBranch != null)
        {
            Commands.Checkout(repo, localBranch);
            return repo.Head.Tip.Sha;
        }

        // Try looking up as a reference (refs/heads/xxx, refs/remotes/origin/xxx)
        var reference = repo.Refs[$"refs/heads/{gitRef}"]
            ?? repo.Refs[$"refs/remotes/origin/{gitRef}"];
        if (reference != null)
        {
            var refTarget = reference.ResolveToDirectReference();
            var commit = repo.Lookup<Commit>(refTarget.TargetIdentifier);
            if (commit != null)
            {
                Commands.Checkout(repo, commit);
                return repo.Head.Tip.Sha;
            }
        }

        // Last resort: iterate branches to find a match
        foreach (var branch in repo.Branches)
        {
            if (branch.FriendlyName == gitRef ||
                branch.FriendlyName == $"origin/{gitRef}" ||
                branch.CanonicalName.EndsWith($"/{gitRef}"))
            {
                Commands.Checkout(repo, branch.Tip);
                return repo.Head.Tip.Sha;
            }
        }

        throw new InvalidOperationException(
            $"Could not find ref '{gitRef}' in repository. " +
            $"Tried as commit/tag, remote branch 'origin/{gitRef}', and local branch.");
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
