using Ilmarinen.Database;
using Ilmarinen.Database.Entities;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
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
    private readonly PriorityRepository _priorities;

    public PipelineRepository(IlmarinenDbContext db, CredentialEncryptionService encryption, PriorityRepository priorities)
    {
        _db = db;
        _encryption = encryption;
        _priorities = priorities;
    }

    public async Task<PipelineInfo> CreateAsync(PipelineSubmission submission)
    {
        var priority = submission.Priority != null
            ? await _priorities.ResolveAsync(PriorityKind.Job, submission.Priority)
            : await _priorities.GetDefaultAsync(PriorityKind.Job);
        var minWorkerPriority = submission.MinWorkerPriority != null ? await _priorities.ResolveMinimumAsync(submission.MinWorkerPriority) : null;

        var pipeline = new Pipeline
        {
            Id = Guid.CreateVersion7(),
            Name = submission.Name,
            RepositoryId = submission.RepositoryId,
            DefaultRef = submission.Ref,
            ScriptPath = submission.ScriptPath,
            CreatedAt = DateTime.UtcNow,
            Schedule = submission.Schedule,
            MinWorkerPriorityId = minWorkerPriority?.Id,
            PriorityId = priority.Id,
            NotificationPolicy = submission.NotificationPolicy
        };

        _db.Pipelines.Add(pipeline);
        await PriorityRepository.WritingReferencesAsync(() => _db.SaveChangesAsync());

        return await GetAsync(pipeline.Id) ?? throw new InvalidOperationException("Pipeline was just created but could not be loaded");
    }

    public async Task<PipelineInfo?> GetAsync(Guid id)
    {
        var pipeline = await _db.Pipelines
            .Include(p => p.Repository)
            .Include(p => p.Priority)
            .Include(p => p.MinWorkerPriority)
            .FirstOrDefaultAsync(p => p.Id == id);

        return pipeline?.Repository != null ? ToPipelineInfo(pipeline, pipeline.Repository) : null;
    }

    /// <summary>Every pipeline, or only those of a job priority, or needing a worker priority.</summary>
    public async Task<IReadOnlyList<PipelineInfo>> GetAllAsync(Guid? priorityId = null, Guid? minWorkerPriorityId = null)
    {
        var pipelines = await _db.Pipelines
            .Where(p => priorityId == null || p.PriorityId == priorityId)
            .Where(p => minWorkerPriorityId == null || p.MinWorkerPriorityId == minWorkerPriorityId)
            .Include(p => p.Repository)
            .Include(p => p.Priority)
            .Include(p => p.MinWorkerPriority)
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
            .Include(p => p.Priority)
            .Include(p => p.MinWorkerPriority)
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
        if (update.MinWorkerPriority != null)
        {
            pipeline.MinWorkerPriorityId = (await _priorities.ResolveMinimumAsync(update.MinWorkerPriority))?.Id;
        }
        if (update.Priority != null)
        {
            pipeline.PriorityId = (await _priorities.ResolveAsync(PriorityKind.Job, update.Priority)).Id;
        }
        if (update.NotificationPolicy != null)
        {
            pipeline.NotificationPolicy = update.NotificationPolicy.Value;
        }

        await PriorityRepository.WritingReferencesAsync(() => _db.SaveChangesAsync());

        // Reload with Repository in case RepositoryId changed, and the priorities in case they did
        _db.ChangeTracker.Clear();
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
            .Include(p => p.Priority)
            .Include(p => p.MinWorkerPriority)
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
        LastTriggeredAt = pipeline.LastTriggeredAt,
        MinWorkerPriority = pipeline.MinWorkerPriority is { } minimum ? PriorityRepository.ToRef(minimum) : null,
        Priority = PriorityRepository.ToRef(pipeline.Priority ?? throw new InvalidOperationException("Pipeline loaded without its priority")),
        NotificationPolicy = pipeline.NotificationPolicy
    };
}
