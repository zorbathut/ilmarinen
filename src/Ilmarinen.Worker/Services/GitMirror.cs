using LibGit2Sharp.Handlers;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System;

namespace Ilmarinen.Worker.Services;

/// <summary>
/// A bare mirror of each repository the worker builds, kept under the workspace root so that each job fetches only what changed and job checkouts can be cloned from it locally.
/// </summary>
public static class GitMirror
{
    /// <summary>
    /// Lives inside the workspace root so it persists with the workspace volume and shares its filesystem, which the hardlinked local clones need. Workspace names can't start with a dot, so this can't collide with one.
    /// </summary>
    private const string MirrorsDirectory = ".mirrors";

    /// <summary>
    /// libgit2 can't repack, so every fetch that brings something new adds a pack, and nothing ever drops force-pushed-away objects. Past this many packs the mirror is rebuilt from scratch instead of fetched into.
    /// </summary>
    private const int MaxPacks = 64;

    private static readonly string[] MirrorRefSpecs =
    [
        "+refs/heads/*:refs/heads/*",
        "+refs/tags/*:refs/tags/*",
    ];

    public static string PathFor(string workspaceRoot, string repoUrl)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RepositoryKey(repoUrl))))[..16].ToLowerInvariant();
        return Path.Combine(workspaceRoot, MirrorsDirectory, $"{hash}.git");
    }

    /// <summary>
    /// Brings the mirror of <paramref name="repoUrl"/> up to date with the remote and returns its path. A missing or overgrown mirror is built afresh; one the fetch fails on is rebuilt only if the remote itself answers, since otherwise the remote is the problem and the mirror is kept.
    /// </summary>
    public static string Sync(string workspaceRoot, string repoUrl, string? gitToken, ILogger logger)
    {
        var path = PathFor(workspaceRoot, repoUrl);
        if (!Directory.Exists(path))
        {
            logger.LogInformation("Creating a mirror of {RepoUrl}", repoUrl);
            Rebuild(workspaceRoot, repoUrl, gitToken);
            return path;
        }

        var packs = CountPacks(path);
        if (packs > MaxPacks)
        {
            logger.LogInformation("The mirror of {RepoUrl} has {Packs} packs; rebuilding it", repoUrl, packs);
            Rebuild(workspaceRoot, repoUrl, gitToken);
            return path;
        }

        try
        {
            using var repo = new Repository(path);
            Fetch(repo, repoUrl, gitToken);
        }
        catch (LibGit2SharpException ex)
        {
            if (!RemoteAnswers(repoUrl, gitToken))
            {
                throw;
            }

            logger.LogWarning(ex, "Fetching into the mirror of {RepoUrl} failed although the remote answers; rebuilding it", repoUrl);
            Rebuild(workspaceRoot, repoUrl, gitToken);
        }

        return path;
    }

    /// <summary>
    /// Replaces the mirror of <paramref name="repoUrl"/> with a fresh one from the remote. The new mirror is built beside the old one and swapped in only once it is complete, so a failure leaves the old mirror as it was.
    /// </summary>
    public static void Rebuild(string workspaceRoot, string repoUrl, string? gitToken)
    {
        var path = PathFor(workspaceRoot, repoUrl);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        DeleteLeftovers(path);

        var building = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            using var repo = new Repository(Repository.Init(building, isBare: true));
            Fetch(repo, repoUrl, gitToken);
            PointHeadAtDefaultBranch(repo, gitToken);
        }
        catch (Exception ex)
        {
            try
            {
                DeleteDirectory(building);
            }
            catch (Exception cleanup)
            {
                throw new AggregateException(ex, cleanup);
            }

            throw;
        }

        if (Directory.Exists(path))
        {
            var old = $"{path}.old-{Guid.NewGuid():N}";
            Directory.Move(path, old);
            Directory.Move(building, path);
            DeleteDirectory(old);
        }
        else
        {
            Directory.Move(building, path);
        }
    }

    /// <summary>
    /// Whether <paramref name="ex"/>, thrown while reading the mirror's own objects, means the mirror is damaged. Anything libgit2 fails at there is, except being handed a malformed ref name, and so is a mirror that failed <see cref="Verify"/>.
    /// </summary>
    public static bool IsDamage(Exception ex)
    {
        return ex is MirrorDamagedException || ex is LibGit2SharpException and not InvalidSpecificationException and not AmbiguousSpecificationException;
    }

    /// <summary>
    /// Checks every object in the mirror, throwing <see cref="MirrorDamagedException"/> (wrapping <paramref name="failure"/>, what prompted the check) if any is missing or corrupt. Costs seconds on a large repository, so it's for diagnosing a failure, not for every job.
    /// </summary>
    public static void Verify(string mirrorPath, Exception failure)
    {
        try
        {
            GitCli.Run(mirrorPath, "fsck", "--no-dangling", "--no-progress");
        }
        catch (GitCommandException check)
        {
            throw new MirrorDamagedException($"The mirror at '{mirrorPath}' is damaged: {check.Message}", failure);
        }
    }

    /// <summary>
    /// Finds the commit <paramref name="gitRef"/> names in the mirror: a commit SHA, tag or branch, in any spelling a clone of the repository accepts (<c>origin/x</c>, <c>refs/remotes/origin/x</c>).
    /// Throws <see cref="InvalidOperationException"/> if nothing matches or if it names a commit no branch or tag reaches, and <see cref="LibGit2SharpException"/> if the ref exists but its commit can't be read.
    /// </summary>
    public static Commit Resolve(Repository mirror, string gitRef)
    {
        // The mirror keeps the remote's branches as its own, so the remote-tracking spellings map onto them
        var name = gitRef;
        if (name.StartsWith("refs/remotes/origin/", StringComparison.Ordinal))
        {
            name = "refs/heads/" + name["refs/remotes/origin/".Length..];
        }
        else if (name.StartsWith("origin/", StringComparison.Ordinal))
        {
            name = name["origin/".Length..];
        }

        var target = mirror.Lookup(name);
        if (target == null)
        {
            // A ref that exists but leads nowhere means the mirror can't read its own objects
            var existing = new[] { name, $"refs/heads/{name}", $"refs/tags/{name}" }.FirstOrDefault(candidate => candidate.StartsWith("refs/", StringComparison.Ordinal) && mirror.Refs[candidate] != null);
            if (existing != null)
            {
                throw new LibGit2SharpException($"The mirror has '{existing}' but can't read the commit it points at.");
            }

            throw new InvalidOperationException($"Could not find ref '{gitRef}' in the repository as a commit, tag or branch.");
        }

        var commit = target as Commit ?? target.Peel<Commit>();
        if (commit.Sha.StartsWith(name, StringComparison.OrdinalIgnoreCase) && !IsReachable(mirror, commit))
        {
            // A persistent workspace fetches from the mirror, and a fetch only carries what the refs reach
            throw new InvalidOperationException($"Commit '{commit.Sha}' is not on any branch or tag of the repository.");
        }

        return commit;
    }

    /// <summary>
    /// Identifies the repository a URL names: user info, scheme, a trailing slash or <c>.git</c> and the host's case don't matter, so spellings of one repository share a mirror, while paths differing in case are different repositories.
    /// </summary>
    internal static string RepositoryKey(string url)
    {
        url = url.Trim().TrimEnd('/');
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            url = url[..^4];
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.IsFile && uri.Host.Length > 0)
        {
            return $"{uri.Host.ToLowerInvariant()}{uri.AbsolutePath}";
        }

        // scp-style `user@host:path`
        var colon = url.IndexOf(':');
        var at = url.IndexOf('@');
        if (colon > 0 && !url.Contains("://") && at < colon)
        {
            return $"{url[(at + 1)..colon].ToLowerInvariant()}/{url[(colon + 1)..].TrimStart('/')}";
        }

        return url;
    }

    /// <summary>
    /// Whether the remote lists its refs. When it doesn't, the fetch failed on the remote's side (unreachable, refusing the credentials, gone), so the caller's own error is the one to report.
    /// </summary>
    private static bool RemoteAnswers(string repoUrl, string? gitToken)
    {
        try
        {
            var credentials = Credentials(gitToken);
            if (credentials == null)
            {
                Repository.ListRemoteReferences(repoUrl);
            }
            else
            {
                Repository.ListRemoteReferences(repoUrl, credentials);
            }

            return true;
        }
        catch (LibGit2SharpException)
        {
            return false;
        }
    }

    private static bool IsReachable(Repository mirror, Commit commit)
    {
        // Branches first: a pinned commit is nearly always on one, which ends the search early
        var refs = mirror.Refs.Where(r => r.IsLocalBranch).Concat(mirror.Refs.Where(r => r.IsTag));
        foreach (var reference in refs)
        {
            var tip = reference.ResolveToDirectReference()?.Target?.Peel<Commit>();
            if (tip != null && (tip.Id == commit.Id || mirror.ObjectDatabase.FindMergeBase(tip, commit)?.Id == commit.Id))
            {
                return true;
            }
        }

        return false;
    }

    private static void Fetch(Repository repo, string repoUrl, string? gitToken)
    {
        var options = new FetchOptions
        {
            Prune = true,
            // libgit2 only honours an explicit `+refs/tags/*:refs/tags/*` refspec when TagFetchMode is also set; without this, force-overwriting a diverged local tag silently no-ops.
            TagFetchMode = TagFetchMode.All,
            CredentialsProvider = Credentials(gitToken),
        };

        // The job's own spelling of the URL is the one fetched from
        if (repo.Network.Remotes["origin"] == null)
        {
            // Configured with the mirror refspec, since libgit2 also updates whatever a remote's configured refspec maps on every fetch from it
            repo.Network.Remotes.Add("origin", repoUrl, MirrorRefSpecs[0]);
        }
        else
        {
            repo.Network.Remotes.Update("origin", remote => remote.Url = repoUrl);
        }

        Commands.Fetch(repo, "origin", MirrorRefSpecs, options, null);
    }

    /// <summary>
    /// A new mirror's HEAD names the worker's own default branch; pointing it at the remote's lets clones of the mirror get <c>origin/HEAD</c> and the default branch an ordinary clone has.
    /// </summary>
    private static void PointHeadAtDefaultBranch(Repository repo, string? gitToken)
    {
        var origin = repo.Network.Remotes["origin"];
        var credentials = Credentials(gitToken);
        var advertised = credentials == null ? repo.Network.ListReferences(origin) : repo.Network.ListReferences(origin, credentials);
        var head = advertised.OfType<SymbolicReference>().FirstOrDefault(r => r.CanonicalName == "HEAD");
        if (head != null && repo.Refs[head.TargetIdentifier] != null)
        {
            repo.Refs.UpdateTarget("HEAD", head.TargetIdentifier);
        }
    }

    private static CredentialsHandler? Credentials(string? gitToken)
    {
        if (string.IsNullOrEmpty(gitToken))
        {
            return null;
        }

        return (url, user, types) => new UsernamePasswordCredentials
        {
            Username = "git",
            Password = gitToken
        };
    }

    private static int CountPacks(string path)
    {
        var packDir = Path.Combine(path, "objects", "pack");
        if (!Directory.Exists(packDir))
        {
            return 0;
        }

        return Directory.GetFiles(packDir, "*.pack").Length;
    }

    /// <summary>
    /// Removes half-built and swapped-out mirrors that an interrupted rebuild left behind.
    /// </summary>
    private static void DeleteLeftovers(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);
        foreach (var leftover in Directory.GetDirectories(directory, $"{name}.tmp-*").Concat(Directory.GetDirectories(directory, $"{name}.old-*")))
        {
            DeleteDirectory(leftover);
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        // Git marks object files read-only, which only stops a delete on Windows; elsewhere the chmod would need ownership of files a step may have chowned through the hardlinks
        if (OperatingSystem.IsWindows())
        {
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
        }

        Directory.Delete(path, recursive: true);
    }
}
