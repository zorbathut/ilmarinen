using System.Collections.Generic;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System;

namespace Ilmarinen.WorkerLauncher;

/// <summary>
/// On-disk bundle layout: {root}/{hash}/ holds an extracted bundle; {hash}.tmp-{random} is an extraction in progress. Extraction is made atomic by extracting to a tmp dir and renaming, so a crash mid-extract never leaves a half-populated bundle directory that looks valid.
/// </summary>
public class BundleCache
{
    private readonly string _root;

    public BundleCache(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    public string BundleDirectory(string hash)
    {
        return Path.Combine(_root, hash);
    }

    public bool IsCached(string hash)
    {
        return Directory.Exists(BundleDirectory(hash));
    }

    /// <summary>The hash of the most recently created cached bundle, for running without a reachable manifest.</summary>
    public string? NewestCachedHash()
    {
        return CachedHashes()
            .OrderByDescending(hash => Directory.GetCreationTimeUtc(BundleDirectory(hash)))
            .FirstOrDefault();
    }

    public void Extract(string zipPath, string hash)
    {
        // Unique tmp name: two launchers sharing a cache volume must not interleave into the same extraction dir.
        var tmpDir = Path.Combine(_root, $"{hash}.tmp-{Guid.NewGuid():N}");
        ZipFile.ExtractToDirectory(zipPath, tmpDir);

        try
        {
            Directory.Move(tmpDir, BundleDirectory(hash));
        }
        catch (IOException) when (IsCached(hash))
        {
            // Another launcher won the race to extract this bundle; theirs is as good as ours.
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    /// <summary>
    /// Removes leftovers from crashed extractions (tmp dirs) and crashed downloads (tmp files). Age-gated so a sibling launcher's in-progress work on a shared volume is never swept.
    /// </summary>
    public void CleanStaleTmp(TimeSpan olderThan)
    {
        var cutoff = DateTime.UtcNow - olderThan;

        foreach (var dir in Directory.GetDirectories(_root, "*.tmp-*"))
        {
            if (Directory.GetCreationTimeUtc(dir) < cutoff)
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        foreach (var file in Directory.GetFiles(_root, "*.tmp-*"))
        {
            if (File.GetCreationTimeUtc(file) < cutoff)
            {
                File.Delete(file);
            }
        }
    }

    public void Prune(string currentHash, string? previousHash)
    {
        foreach (var hash in SelectPrunable(CachedHashes(), currentHash, previousHash))
        {
            Directory.Delete(BundleDirectory(hash), recursive: true);
        }
    }

    /// <summary>Keep the current bundle and the previous one (the instant-rollback candidate); everything else is prunable.</summary>
    public static IReadOnlyList<string> SelectPrunable(IEnumerable<string> cachedHashes, string currentHash, string? previousHash)
    {
        return cachedHashes
            .Where(hash => hash != currentHash && hash != previousHash)
            .ToList();
    }

    private List<string> CachedHashes()
    {
        return Directory.GetDirectories(_root)
            .Select(Path.GetFileName)
            .Where(name => name != null && !name.Contains(".tmp-"))
            .Select(name => name!)
            .ToList();
    }
}
