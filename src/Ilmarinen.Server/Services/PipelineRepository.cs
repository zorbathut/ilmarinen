using Ilmarinen.Database;
using Ilmarinen.Database.Entities;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Microsoft.EntityFrameworkCore;
using NUlid;

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
            Id = Ulid.NewUlid(),
            Name = submission.Name,
            RepoUrl = submission.RepoUrl,
            DefaultRef = submission.Ref,
            ScriptPath = submission.ScriptPath,
            EncryptedGitToken = _encryption.Encrypt(submission.GitToken),
            CreatedAt = DateTime.UtcNow,
            Schedule = submission.Schedule
        };

        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync();

        return ToPipelineInfo(pipeline);
    }

    public async Task<PipelineInfo?> GetAsync(Ulid id)
    {
        var pipeline = await _db.Pipelines.FirstOrDefaultAsync(p => p.Id == id);
        return pipeline != null ? ToPipelineInfo(pipeline) : null;
    }

    public async Task<IReadOnlyList<PipelineInfo>> GetAllAsync()
    {
        var pipelines = await _db.Pipelines
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();

        return pipelines.Select(ToPipelineInfo).ToList();
    }

    public async Task<bool> DeleteAsync(Ulid id)
    {
        var rows = await _db.Pipelines
            .Where(p => p.Id == id)
            .ExecuteDeleteAsync();

        return rows > 0;
    }

    public async Task<PipelineInfo?> UpdateAsync(Ulid id, PipelineUpdate update)
    {
        var pipeline = await _db.Pipelines.FirstOrDefaultAsync(p => p.Id == id);
        if (pipeline == null) return null;

        if (update.Name != null)
            pipeline.Name = update.Name;
        if (update.RepoUrl != null)
            pipeline.RepoUrl = update.RepoUrl;
        if (update.Ref != null)
            pipeline.DefaultRef = update.Ref;
        if (update.ScriptPath != null)
            pipeline.ScriptPath = update.ScriptPath;
        if (update.UpdateGitToken)
            pipeline.EncryptedGitToken = _encryption.Encrypt(string.IsNullOrEmpty(update.GitToken) ? null : update.GitToken);
        if (update.ClearSchedule)
            pipeline.Schedule = null;
        else if (update.Schedule != null)
            pipeline.Schedule = update.Schedule;

        await _db.SaveChangesAsync();
        return ToPipelineInfo(pipeline);
    }

    public async Task<JobSubmission?> BuildSubmissionAsync(Ulid id, string? refOverride)
    {
        var pipeline = await _db.Pipelines.FirstOrDefaultAsync(p => p.Id == id);
        if (pipeline == null) return null;

        return new JobSubmission
        {
            RepoUrl = pipeline.RepoUrl,
            Ref = refOverride ?? pipeline.DefaultRef,
            ScriptPath = pipeline.ScriptPath,
            GitToken = _encryption.Decrypt(pipeline.EncryptedGitToken)
        };
    }

    public async Task<IReadOnlyList<Pipeline>> GetScheduledPipelinesAsync()
    {
        return await _db.Pipelines
            .Where(p => p.Schedule != null)
            .ToListAsync();
    }

    public async Task UpdateLastTriggeredAtAsync(Ulid id, DateTime triggeredAt)
    {
        await _db.Pipelines
            .Where(p => p.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LastTriggeredAt, triggeredAt));
    }

    private static PipelineInfo ToPipelineInfo(Pipeline pipeline) => new()
    {
        Id = pipeline.Id,
        Name = pipeline.Name,
        RepoUrl = pipeline.RepoUrl,
        DefaultRef = pipeline.DefaultRef,
        ScriptPath = pipeline.ScriptPath,
        HasGitToken = pipeline.EncryptedGitToken != null,
        CreatedAt = pipeline.CreatedAt,
        Schedule = pipeline.Schedule,
        LastTriggeredAt = pipeline.LastTriggeredAt
    };
}
