using Ilmarinen.Database.Entities;
using Ilmarinen.Database;
using Ilmarinen.Protocol.Responses;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Server.Services;

public class ArtifactRepository
{
    private readonly IlmarinenDbContext _db;
    private readonly string _storagePath;

    public ArtifactRepository(IlmarinenDbContext db, ServerConfig config)
    {
        _db = db;
        _storagePath = config.ArtifactStoragePath;
    }

    public async Task<ArtifactInfo> SaveAsync(Guid jobId, string name, long size, Stream content)
    {
        var artifactId = Guid.CreateVersion7();
        var relativePath = $"{jobId}/{artifactId}-{SanitizeFileName(name)}";
        var fullPath = Path.Combine(_storagePath, relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using var fileStream = File.Create(fullPath);
        await content.CopyToAsync(fileStream);

        var artifact = new JobArtifact
        {
            Id = artifactId,
            JobId = jobId,
            Name = name,
            RelativePath = relativePath,
            Size = size,
            CreatedAt = DateTime.UtcNow
        };

        _db.JobArtifacts.Add(artifact);
        await _db.SaveChangesAsync();

        return ToArtifactInfo(artifact);
    }

    public async Task<ArtifactInfo?> GetAsync(Guid id)
    {
        var artifact = await _db.JobArtifacts.FirstOrDefaultAsync(a => a.Id == id);
        return artifact != null ? ToArtifactInfo(artifact) : null;
    }

    public async Task<IReadOnlyList<ArtifactInfo>> GetByJobIdAsync(Guid jobId)
    {
        var artifacts = await _db.JobArtifacts
            .Where(a => a.JobId == jobId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync();

        return artifacts.Select(ToArtifactInfo).ToList();
    }

    public async Task<(Stream? Stream, string? FileName)> GetContentAsync(Guid id)
    {
        var artifact = await _db.JobArtifacts.FirstOrDefaultAsync(a => a.Id == id);
        if (artifact == null) return (null, null);

        var fullPath = Path.Combine(_storagePath, artifact.RelativePath);
        if (!File.Exists(fullPath)) return (null, null);

        return (File.OpenRead(fullPath), artifact.Name);
    }

    /// <summary>Deletes stored artifact files whose rows are already gone. A file that can't be deleted is left behind and reported in what this returns, the paths it couldn't delete.</summary>
    public IReadOnlyList<(string Path, Exception Error)> DeleteFiles(IEnumerable<string> relativePaths)
    {
        var failed = new List<(string, Exception)>();
        var directories = new HashSet<string>();
        foreach (var relativePath in relativePaths)
        {
            var fullPath = Path.Combine(_storagePath, relativePath);
            try
            {
                File.Delete(fullPath);
                directories.Add(Path.GetDirectoryName(fullPath)!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add((relativePath, ex));
            }
        }

        // A job's artifacts share its directory; once they are all gone, so is it.
        foreach (var directory in directories.Where(d => Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()))
        {
            try
            {
                Directory.Delete(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add((Path.GetRelativePath(_storagePath, directory), ex));
            }
        }
        return failed;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }

    private static ArtifactInfo ToArtifactInfo(JobArtifact artifact) => new()
    {
        Id = artifact.Id,
        Name = artifact.Name,
        Size = artifact.Size,
        CreatedAt = artifact.CreatedAt
    };
}
