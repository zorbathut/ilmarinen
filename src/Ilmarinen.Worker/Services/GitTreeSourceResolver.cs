using LibGit2Sharp;
using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.IO;
using System;

namespace Ilmarinen.Worker.Services;

/// <summary>
/// Serves a pipeline script and the files it <c>#load</c>s from one commit's tree, so the script can be compiled without checking the commit out. The commit's repository must stay open while the script compiles.
/// </summary>
public sealed class GitTreeSourceResolver : SourceReferenceResolver
{
    /// <summary>
    /// Scripts are seen at their repository paths under this root. It names no real directory, so a relative <c>#r</c> finds nothing rather than an unrelated file on the host.
    /// </summary>
    public const string VirtualRoot = "/ilmarinen-repo/";

    /// <summary>
    /// As in git itself, a chain of symbolic links longer than this is taken to be a loop.
    /// </summary>
    private const int MaxSymlinkHops = 40;

    private readonly Commit _commit;

    public GitTreeSourceResolver(Commit commit)
    {
        _commit = commit;
    }

    /// <summary>
    /// The path a script at <paramref name="repoPath"/> is compiled under, or null if the path is absolute or leaves the repository.
    /// </summary>
    public string? FilePathFor(string repoPath)
    {
        return Resolve(repoPath, null);
    }

    /// <summary>
    /// Whether <paramref name="filePath"/> (from <see cref="FilePathFor"/>) names anything in the commit.
    /// </summary>
    public bool Exists(string filePath)
    {
        return _commit.Tree[ToRepoPath(filePath)] != null;
    }

    public override string? NormalizePath(string path, string? baseFilePath)
    {
        return Resolve(path, baseFilePath);
    }

    public override string? ResolveReference(string path, string? baseFilePath)
    {
        var resolved = Resolve(path, baseFilePath);
        if (resolved == null || !Exists(resolved))
        {
            return null;
        }

        return resolved;
    }

    public override Stream OpenRead(string resolvedPath)
    {
        var blob = ReadBlob(resolvedPath);

        var buffer = new MemoryStream();
        using (var content = blob.GetContentStream())
        {
            content.CopyTo(buffer);
        }

        buffer.Position = 0;
        return buffer;
    }

    public override bool Equals(object? other)
    {
        return other is GitTreeSourceResolver resolver && resolver._commit.Id == _commit.Id;
    }

    public override int GetHashCode()
    {
        return _commit.Id.GetHashCode();
    }

    /// <summary>
    /// The file at <paramref name="filePath"/>, following symbolic links as a checkout would, as long as they stay inside the repository.
    /// </summary>
    private Blob ReadBlob(string filePath)
    {
        for (var hop = 0; hop <= MaxSymlinkHops; hop++)
        {
            var repoPath = ToRepoPath(filePath);
            var entry = _commit.Tree[repoPath] ?? throw new FileNotFoundException($"'{repoPath}' is not in commit {_commit.Sha}.", repoPath);
            if (entry.Target is not Blob blob)
            {
                throw new IOException($"'{repoPath}' is a {entry.TargetType.ToString().ToLowerInvariant()} in the repository, not a file.");
            }

            if (entry.Mode != Mode.SymbolicLink)
            {
                return blob;
            }

            var linkTarget = blob.GetContentText();
            filePath = Resolve(linkTarget, filePath) ?? throw new IOException($"'{repoPath}' is a symbolic link to '{linkTarget}', which is outside the repository.");
        }

        throw new IOException($"'{ToRepoPath(filePath)}' is reached through more than {MaxSymlinkHops} symbolic links.");
    }

    /// <summary>
    /// Resolves <paramref name="path"/> against the directory of <paramref name="baseFilePath"/> (or the repository root), giving null for an absolute path or one that climbs out of the repository.
    /// </summary>
    private static string? Resolve(string path, string? baseFilePath)
    {
        if (path.Length == 0 || path.StartsWith('/') || path.Contains('\\') || path.Contains(':'))
        {
            return null;
        }

        var segments = new List<string>();
        if (baseFilePath != null)
        {
            segments.AddRange(ToRepoPath(baseFilePath).Split('/'));
            segments.RemoveAt(segments.Count - 1);
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    return null;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            return null;
        }

        return VirtualRoot + string.Join('/', segments);
    }

    private static string ToRepoPath(string filePath)
    {
        if (filePath.StartsWith(VirtualRoot, StringComparison.Ordinal))
        {
            return filePath[VirtualRoot.Length..];
        }

        return filePath;
    }
}
