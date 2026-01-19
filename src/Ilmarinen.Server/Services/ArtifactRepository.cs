using Ilmarinen.Database;
using Ilmarinen.Database.Entities;
using Ilmarinen.Protocol.Responses;
using Microsoft.EntityFrameworkCore;
using NUlid;

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

    public async Task<ArtifactInfo> SaveAsync(Ulid jobId, string name, long size, Stream content)
    {
        var artifactId = Ulid.NewUlid();
        var relativePath = $"{jobId}/{artifactId}-{SanitizeFileName(name)}";
        var fullPath = Path.Combine(_storagePath, relativePath);

        // Ensure directory exists
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        // Stream content to disk
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

    public async Task<ArtifactInfo?> GetAsync(Ulid id)
    {
        var artifact = await _db.JobArtifacts.FirstOrDefaultAsync(a => a.Id == id);
        return artifact != null ? ToArtifactInfo(artifact) : null;
    }

    public async Task<IReadOnlyList<ArtifactInfo>> GetByJobIdAsync(Ulid jobId)
    {
        var artifacts = await _db.JobArtifacts
            .Where(a => a.JobId == jobId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync();

        return artifacts.Select(ToArtifactInfo).ToList();
    }

    public async Task<(Stream? Stream, string? FileName)> GetContentAsync(Ulid id)
    {
        var artifact = await _db.JobArtifacts.FirstOrDefaultAsync(a => a.Id == id);
        if (artifact == null) return (null, null);

        var fullPath = Path.Combine(_storagePath, artifact.RelativePath);
        if (!File.Exists(fullPath)) return (null, null);

        return (File.OpenRead(fullPath), artifact.Name);
    }

    public string GetFullPath(string relativePath) => Path.Combine(_storagePath, relativePath);

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
