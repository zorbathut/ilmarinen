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

public class PipelineRepository
{
    private readonly IlmarinenDbContext _db;
    private readonly CredentialEncryptionService _encryption;

    public PipelineRepository(IlmarinenDbContext db, CredentialEncryptionService encryption)
    {
        _db = db;
        _encryption = encryption;
    }

    public async Task<PipelineInfo> CreateAsync(PipelineSubmission submission)
    {
        var pipeline = new Pipeline
        {
            Id = Guid.CreateVersion7(),
            Name = submission.Name,
            RepositoryId = submission.RepositoryId,
            DefaultRef = submission.Ref,
            ScriptPath = submission.ScriptPath,
            CreatedAt = DateTime.UtcNow,
            Schedule = submission.Schedule
        };

        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync();

        return await GetAsync(pipeline.Id) ?? throw new InvalidOperationException("Pipeline was just created but could not be loaded");
    }

    public async Task<PipelineInfo?> GetAsync(Guid id)
    {
        var pipeline = await _db.Pipelines
            .Include(p => p.Repository)
            .FirstOrDefaultAsync(p => p.Id == id);

        return pipeline?.Repository != null ? ToPipelineInfo(pipeline, pipeline.Repository) : null;
    }

    public async Task<IReadOnlyList<PipelineInfo>> GetAllAsync()
    {
        var pipelines = await _db.Pipelines
            .Include(p => p.Repository)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();

        return pipelines
            .Where(p => p.Repository != null)
            .Select(p => ToPipelineInfo(p, p.Repository!))
            .ToList();
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var rows = await _db.Pipelines
            .Where(p => p.Id == id)
            .ExecuteDeleteAsync();

        return rows > 0;
    }

    public async Task<PipelineInfo?> UpdateAsync(Guid id, PipelineUpdate update)
    {
        var pipeline = await _db.Pipelines
            .Include(p => p.Repository)
            .FirstOrDefaultAsync(p => p.Id == id);
        if (pipeline == null) return null;

        if (update.Name != null)
            pipeline.Name = update.Name;
        if (update.RepositoryId != null)
            pipeline.RepositoryId = update.RepositoryId.Value;
        if (update.Ref != null)
            pipeline.DefaultRef = update.Ref;
        if (update.ScriptPath != null)
            pipeline.ScriptPath = update.ScriptPath;
        if (update.ClearSchedule)
            pipeline.Schedule = null;
        else if (update.Schedule != null)
            pipeline.Schedule = update.Schedule;

        await _db.SaveChangesAsync();

        // Reload with Repository in case RepositoryId changed
        return await GetAsync(id);
    }

    public async Task<bool> ExistsAsync(Guid id)
    {
        return await _db.Pipelines.AnyAsync(p => p.Id == id);
    }

    public async Task<IReadOnlyList<Pipeline>> GetScheduledPipelinesAsync()
    {
        return await _db.Pipelines
            .Include(p => p.Repository)
            .Where(p => p.Schedule != null)
            .ToListAsync();
    }

    public async Task UpdateLastTriggeredAtAsync(Guid id, DateTime triggeredAt)
    {
        await _db.Pipelines
            .Where(p => p.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LastTriggeredAt, triggeredAt));
    }

    private static PipelineInfo ToPipelineInfo(Pipeline pipeline, Repository repo) => new()
    {
        Id = pipeline.Id,
        Name = pipeline.Name,
        RepositoryId = repo.Id,
        RepositoryName = repo.Name,
        RepoUrl = repo.RepoUrl,
        DefaultRef = pipeline.DefaultRef,
        ScriptPath = pipeline.ScriptPath,
        HasGitToken = repo.EncryptedGitToken != null,
        CreatedAt = pipeline.CreatedAt,
        Schedule = pipeline.Schedule,
        LastTriggeredAt = pipeline.LastTriggeredAt
    };
}
