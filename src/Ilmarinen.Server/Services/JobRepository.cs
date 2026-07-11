using Ilmarinen.Database.Entities;
using Ilmarinen.Database;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Server.Services;

public class JobRepository
{
    private readonly IlmarinenDbContext _db;
    private readonly ArtifactRepository _artifacts;
    private readonly CredentialEncryptionService _encryption;

    public JobRepository(IlmarinenDbContext db, ArtifactRepository artifacts, CredentialEncryptionService encryption)
    {
        _db = db;
        _artifacts = artifacts;
        _encryption = encryption;
    }

    public async Task<JobInfo> CreateAsync(JobSubmission submission)
    {
        // Resolve fields from pipeline if PipelineId is set
        string? resolvedRepoUrl = submission.RepoUrl;
        string? resolvedRef = submission.Ref;
        string? resolvedScriptPath = submission.ScriptPath;
        string? resolvedGitToken = null;

        if (submission.PipelineId != null)
        {
            var pipeline = await _db.Pipelines
                .Include(p => p.Repository)
                .FirstOrDefaultAsync(p => p.Id == submission.PipelineId.Value);

            if (pipeline == null)
                throw new ArgumentException($"Pipeline {submission.PipelineId} not found");

            resolvedRepoUrl ??= pipeline.Repository?.RepoUrl;
            resolvedRef ??= pipeline.DefaultRef;
            resolvedScriptPath ??= pipeline.ScriptPath;

            if (submission.GitTokenMode == GitTokenMode.Inherit)
                resolvedGitToken = _encryption.Decrypt(pipeline.Repository?.EncryptedGitToken);
        }

        if (submission.GitTokenMode == GitTokenMode.Explicit)
            resolvedGitToken = submission.GitToken;

        if (string.IsNullOrEmpty(resolvedRepoUrl))
            throw new ArgumentException("RepoUrl is required (either directly or via PipelineId)");
        if (string.IsNullOrEmpty(resolvedRef))
            throw new ArgumentException("Ref is required (either directly or via PipelineId)");
        if (string.IsNullOrEmpty(resolvedScriptPath))
            throw new ArgumentException("ScriptPath is required (either directly or via PipelineId)");

        var job = new Job
        {
            Id = Guid.CreateVersion7(),
            Status = JobStatus.Pending,
            RepoUrl = resolvedRepoUrl,
            Ref = resolvedRef,
            ScriptPath = resolvedScriptPath,
            CreatedAt = DateTime.UtcNow,
            EncryptedGitToken = _encryption.Encrypt(resolvedGitToken),
            GitTokenMode = submission.GitTokenMode,
            PipelineId = submission.PipelineId
        };

        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        return ToJobInfo(job);
    }

    public async Task<JobInfo?> GetAsync(Guid id)
    {
        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.Id == id);
        if (job == null) return null;

        var artifacts = await _artifacts.GetByJobIdAsync(id);
        var workerName = job.WorkerId != null
            ? await _db.Workers.Where(w => w.Id == job.WorkerId).Select(w => w.Name).FirstOrDefaultAsync()
            : null;
        var pipelineName = job.PipelineId != null
            ? await _db.Pipelines.Where(p => p.Id == job.PipelineId).Select(p => p.Name).FirstOrDefaultAsync()
            : null;

        // For single-job fetch, check pipeline+repo existence for retry eligibility
        HashSet<Guid>? retryablePipelines = null;
        if (job.PipelineId != null && job.GitTokenMode == GitTokenMode.Inherit)
        {
            var exists = await _db.Pipelines
                .Where(p => p.Id == job.PipelineId && p.Repository != null)
                .AnyAsync();
            retryablePipelines = exists ? new HashSet<Guid> { job.PipelineId.Value } : new HashSet<Guid>();
        }

        return ToJobInfo(job, artifacts, workerName, pipelineName, retryablePipelines);
    }

    public async Task<JobSubmission?> GetSubmissionAsync(Guid id)
    {
        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.Id == id);
        return job != null ? new JobSubmission
        {
            PipelineId = job.PipelineId,
            RepoUrl = job.RepoUrl,
            Ref = job.Ref,
            ScriptPath = job.ScriptPath,
            GitTokenMode = job.GitTokenMode,
            GitToken = _encryption.Decrypt(job.EncryptedGitToken)
        } : null;
    }

    /// <summary>
    /// Creates a new job by retrying a completed (failed/cancelled) job.
    /// Returns null if the job doesn't exist or isn't in a terminal state.
    /// Throws if retry is blocked (e.g. explicit token was cleared).
    /// </summary>
    public async Task<JobSubmission?> BuildRetrySubmissionAsync(Guid id)
    {
        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.Id == id);
        if (job == null) return null;

        if (job.Status is not (JobStatus.Failed or JobStatus.Cancelled))
            return null;

        switch (job.GitTokenMode)
        {
            case GitTokenMode.None:
                return new JobSubmission
                {
                    PipelineId = job.PipelineId,
                    RepoUrl = job.RepoUrl,
                    Ref = job.Ref,
                    ScriptPath = job.ScriptPath,
                    GitTokenMode = GitTokenMode.None
                };

            case GitTokenMode.Inherit:
            {
                if (job.PipelineId == null)
                    throw new InvalidOperationException("Job has Inherit token mode but no pipeline");

                var pipelineExists = await _db.Pipelines
                    .Where(p => p.Id == job.PipelineId && p.Repository != null)
                    .AnyAsync();

                if (!pipelineExists)
                    throw new InvalidOperationException("Pipeline or its repository no longer exists");

                return new JobSubmission
                {
                    PipelineId = job.PipelineId,
                    Ref = job.Ref,
                    ScriptPath = job.ScriptPath,
                    GitTokenMode = GitTokenMode.Inherit
                };
            }

            case GitTokenMode.Explicit:
                throw new InvalidOperationException("Cannot retry: explicit git token was cleared after job completion");

            default:
                throw new InvalidOperationException($"Unknown GitTokenMode: {job.GitTokenMode}");
        }
    }

    public async Task UpdateStatusAsync(Guid id, JobStatus status, Guid? workerId = null)
    {
        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.Id == id);
        if (job == null) return;

        // Success and Failed are truly final — nothing overrides them.
        if (job.Status is JobStatus.Success or JobStatus.Failed)
            return;

        // Cancelled can be overridden by Success or Failed: if the worker actually completed the job before the cancel reached it, the real outcome wins.
        if (job.Status == JobStatus.Cancelled && status is not (JobStatus.Success or JobStatus.Failed))
            return;

        job.Status = status;
        if (workerId != null) job.WorkerId = workerId;

        if (status == JobStatus.Running)
            job.StartedAt = DateTime.UtcNow;

        if (status is JobStatus.Success or JobStatus.Failed or JobStatus.Cancelled)
        {
            job.CompletedAt = DateTime.UtcNow;
            job.EncryptedGitToken = null; // Clear token after job completion
        }

        await _db.SaveChangesAsync();
    }

    public async Task SetCommitAsync(Guid id, string commitSha)
    {
        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.Id == id);
        if (job == null) return;

        job.Commit = commitSha;
        await _db.SaveChangesAsync();
    }

    public async Task<Guid?> GetRunningJobForWorkerAsync(Guid workerId)
    {
        return await _db.Jobs
            .Where(j => j.WorkerId == workerId && j.Status == JobStatus.Running)
            .Select(j => (Guid?)j.Id)
            .FirstOrDefaultAsync();
    }

    public async Task<IReadOnlyList<JobInfo>> GetAllAsync()
    {
        var workerNames = await _db.Workers.ToDictionaryAsync(w => w.Id, w => w.Name);
        var pipelineNames = await _db.Pipelines.ToDictionaryAsync(p => p.Id, p => p.Name);

        var jobs = await _db.Jobs
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync();

        var retryablePipelines = await GetRetryablePipelineIdsAsync();

        return jobs.Select(j => ToJobInfo(j,
            workerName: j.WorkerId != null && workerNames.TryGetValue(j.WorkerId.Value, out var wName) ? wName : null,
            pipelineName: j.PipelineId != null && pipelineNames.TryGetValue(j.PipelineId.Value, out var pName) ? pName : null,
            retryablePipelines: retryablePipelines)).ToList();
    }

    public async Task<IReadOnlyList<JobInfo>> GetByPipelineAsync(Guid pipelineId)
    {
        var workerNames = await _db.Workers.ToDictionaryAsync(w => w.Id, w => w.Name);
        var pipelineName = await _db.Pipelines.Where(p => p.Id == pipelineId).Select(p => p.Name).FirstOrDefaultAsync();

        var jobs = await _db.Jobs
            .Where(j => j.PipelineId == pipelineId)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync();

        var retryablePipelines = await GetRetryablePipelineIdsAsync();

        return jobs.Select(j => ToJobInfo(j,
            workerName: j.WorkerId != null && workerNames.TryGetValue(j.WorkerId.Value, out var wName) ? wName : null,
            pipelineName: pipelineName,
            retryablePipelines: retryablePipelines)).ToList();
    }

    public async Task<IReadOnlyList<JobInfo>> GetByWorkerAsync(Guid workerId)
    {
        var workerName = await _db.Workers.Where(w => w.Id == workerId).Select(w => w.Name).FirstOrDefaultAsync();
        var pipelineNames = await _db.Pipelines.ToDictionaryAsync(p => p.Id, p => p.Name);

        var jobs = await _db.Jobs
            .Where(j => j.WorkerId == workerId)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync();

        var retryablePipelines = await GetRetryablePipelineIdsAsync();

        return jobs.Select(j => ToJobInfo(j,
            workerName: workerName,
            pipelineName: j.PipelineId != null && pipelineNames.TryGetValue(j.PipelineId.Value, out var pName) ? pName : null,
            retryablePipelines: retryablePipelines)).ToList();
    }

    public async Task<IReadOnlyList<Guid>> GetQueuedJobIdsAsync()
    {
        return await _db.Jobs
            .Where(j => j.Status == JobStatus.Queued)
            .OrderBy(j => j.CreatedAt)
            .Select(j => j.Id)
            .ToListAsync();
    }

    public async Task<bool> TryCancelAsync(Guid id)
    {
        var now = DateTime.UtcNow;
        var rows = await _db.Jobs
            .Where(j => j.Id == id)
            .Where(j => j.Status != JobStatus.Success
                     && j.Status != JobStatus.Failed
                     && j.Status != JobStatus.Cancelled)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Cancelled)
                .SetProperty(j => j.CompletedAt, now));

        return rows > 0;
    }

    /// <summary>
    /// Returns the set of pipeline IDs whose repository still exists.
    /// Used to determine retry eligibility for Inherit-mode jobs.
    /// </summary>
    private async Task<HashSet<Guid>> GetRetryablePipelineIdsAsync()
    {
        var ids = await _db.Pipelines
            .Where(p => p.Repository != null)
            .Select(p => p.Id)
            .ToListAsync();

        return ids.ToHashSet();
    }

    /// <summary>
    /// Computes retry eligibility for a job.
    /// </summary>
    private static (bool canRetry, string? reason) ComputeRetryEligibility(Job job, HashSet<Guid>? retryablePipelines)
    {
        // Only terminal jobs can be retried
        if (job.Status is not (JobStatus.Failed or JobStatus.Cancelled))
            return (false, null);

        switch (job.GitTokenMode)
        {
            case GitTokenMode.None:
                return (true, null);

            case GitTokenMode.Inherit:
                if (job.PipelineId == null)
                    return (false, "Pipeline reference is missing");
                if (retryablePipelines != null && !retryablePipelines.Contains(job.PipelineId.Value))
                    return (false, "Pipeline or its repository no longer exists");
                // If retryablePipelines is null (not loaded), assume retryable
                return (true, null);

            case GitTokenMode.Explicit:
                return (false, "Explicit git token was cleared after completion");

            default:
                return (false, $"Unknown token mode: {job.GitTokenMode}");
        }
    }

    private static JobInfo ToJobInfo(Job job, IReadOnlyList<ArtifactInfo>? artifacts = null, string? workerName = null, string? pipelineName = null, HashSet<Guid>? retryablePipelines = null)
    {
        var (canRetry, retryBlockedReason) = ComputeRetryEligibility(job, retryablePipelines);

        return new()
        {
            Id = job.Id,
            Status = job.Status,
            RepoUrl = job.RepoUrl,
            Ref = job.Ref,
            Commit = job.Commit,
            ScriptPath = job.ScriptPath,
            WorkerId = job.WorkerId,
            WorkerName = workerName,
            CreatedAt = job.CreatedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            Artifacts = artifacts,
            GitTokenMode = job.GitTokenMode,
            PipelineId = job.PipelineId,
            PipelineName = pipelineName,
            CanRetry = canRetry,
            RetryBlockedReason = retryBlockedReason
        };
    }
}
