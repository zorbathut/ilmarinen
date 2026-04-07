using Ilmarinen.Database;
using Ilmarinen.Database.Entities;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Ilmarinen.Server.Services;

public class RepositoryRepository
{
    private readonly IlmarinenDbContext _db;
    private readonly CredentialEncryptionService _encryption;

    public RepositoryRepository(IlmarinenDbContext db, CredentialEncryptionService encryption)
    {
        _db = db;
        _encryption = encryption;
    }

    public async Task<RepositoryInfo> CreateAsync(RepositorySubmission submission)
    {
        var repo = new Repository
        {
            Id = Guid.CreateVersion7(),
            Name = submission.Name,
            RepoUrl = submission.RepoUrl,
            EncryptedGitToken = _encryption.Encrypt(submission.GitToken),
            CreatedAt = DateTime.UtcNow
        };

        _db.Repositories.Add(repo);
        await _db.SaveChangesAsync();

        return ToRepositoryInfo(repo);
    }

    public async Task<RepositoryInfo?> GetAsync(Guid id)
    {
        var repo = await _db.Repositories.FirstOrDefaultAsync(r => r.Id == id);
        return repo != null ? ToRepositoryInfo(repo) : null;
    }

    public async Task<IReadOnlyList<RepositoryInfo>> GetAllAsync()
    {
        var repos = await _db.Repositories
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        return repos.Select(ToRepositoryInfo).ToList();
    }

    public async Task<RepositoryInfo?> UpdateAsync(Guid id, RepositoryUpdate update)
    {
        var repo = await _db.Repositories.FirstOrDefaultAsync(r => r.Id == id);
        if (repo == null) return null;

        if (update.Name != null)
            repo.Name = update.Name;
        if (update.RepoUrl != null)
            repo.RepoUrl = update.RepoUrl;
        if (update.UpdateGitToken)
            repo.EncryptedGitToken = _encryption.Encrypt(string.IsNullOrEmpty(update.GitToken) ? null : update.GitToken);

        await _db.SaveChangesAsync();
        return ToRepositoryInfo(repo);
    }

    /// <summary>
    /// Returns null if not found, the count of blocking pipelines if any exist, or 0 on success.
    /// </summary>
    public async Task<int?> DeleteAsync(Guid id)
    {
        var repo = await _db.Repositories.FirstOrDefaultAsync(r => r.Id == id);
        if (repo == null) return null;

        var pipelineCount = await _db.Pipelines.CountAsync(p => p.RepositoryId == id);
        if (pipelineCount > 0) return pipelineCount;

        _db.Repositories.Remove(repo);
        await _db.SaveChangesAsync();
        return 0;
    }

    private static RepositoryInfo ToRepositoryInfo(Repository repo) => new()
    {
        Id = repo.Id,
        Name = repo.Name,
        RepoUrl = repo.RepoUrl,
        HasGitToken = repo.EncryptedGitToken != null,
        CreatedAt = repo.CreatedAt
    };
}
