using Ilmarinen.Database.Entities;
using Ilmarinen.Database;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Microsoft.EntityFrameworkCore;
using NUlid;
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
            Id = Ulid.NewUlid(),
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

    public async Task<JobInfo?> GetAsync(Ulid id)
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
        return ToJobInfo(job, artifacts, workerName, pipelineName);
    }

    public async Task<JobSubmission?> GetSubmissionAsync(Ulid id)
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

    public async Task UpdateStatusAsync(Ulid id, JobStatus status, Ulid? workerId = null)
    {
        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.Id == id);
        if (job == null) return;

        // Success and Failed are truly final — nothing overrides them.
        if (job.Status is JobStatus.Success or JobStatus.Failed)
            return;

        // Cancelled can be overridden by Success or Failed: if the worker actually
        // completed the job before the cancel reached it, the real outcome wins.
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

    public async Task SetCommitAsync(Ulid id, string commitSha)
    {
        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.Id == id);
        if (job == null) return;

        job.Commit = commitSha;
        await _db.SaveChangesAsync();
    }

    public async Task<Ulid?> GetRunningJobForWorkerAsync(Ulid workerId)
    {
        return await _db.Jobs
            .Where(j => j.WorkerId == workerId && j.Status == JobStatus.Running)
            .Select(j => (Ulid?)j.Id)
            .FirstOrDefaultAsync();
    }

    public async Task<IReadOnlyList<JobInfo>> GetAllAsync()
    {
        var workerNames = await _db.Workers.ToDictionaryAsync(w => w.Id, w => w.Name);
        var pipelineNames = await _db.Pipelines.ToDictionaryAsync(p => p.Id, p => p.Name);

        var jobs = await _db.Jobs
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync();

        return jobs.Select(j => ToJobInfo(j,
            workerName: j.WorkerId != null && workerNames.TryGetValue(j.WorkerId.Value, out var wName) ? wName : null,
            pipelineName: j.PipelineId != null && pipelineNames.TryGetValue(j.PipelineId.Value, out var pName) ? pName : null)).ToList();
    }

    public async Task<IReadOnlyList<JobInfo>> GetByPipelineAsync(Ulid pipelineId)
    {
        var workerNames = await _db.Workers.ToDictionaryAsync(w => w.Id, w => w.Name);
        var pipelineName = await _db.Pipelines.Where(p => p.Id == pipelineId).Select(p => p.Name).FirstOrDefaultAsync();

        var jobs = await _db.Jobs
            .Where(j => j.PipelineId == pipelineId)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync();

        return jobs.Select(j => ToJobInfo(j,
            workerName: j.WorkerId != null && workerNames.TryGetValue(j.WorkerId.Value, out var wName) ? wName : null,
            pipelineName: pipelineName)).ToList();
    }

    public async Task<IReadOnlyList<JobInfo>> GetByWorkerAsync(Ulid workerId)
    {
        var workerName = await _db.Workers.Where(w => w.Id == workerId).Select(w => w.Name).FirstOrDefaultAsync();
        var pipelineNames = await _db.Pipelines.ToDictionaryAsync(p => p.Id, p => p.Name);

        var jobs = await _db.Jobs
            .Where(j => j.WorkerId == workerId)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync();

        return jobs.Select(j => ToJobInfo(j,
            workerName: workerName,
            pipelineName: j.PipelineId != null && pipelineNames.TryGetValue(j.PipelineId.Value, out var pName) ? pName : null)).ToList();
    }

    public async Task<IReadOnlyList<Ulid>> GetQueuedJobIdsAsync()
    {
        return await _db.Jobs
            .Where(j => j.Status == JobStatus.Queued)
            .OrderBy(j => j.CreatedAt)
            .Select(j => j.Id)
            .ToListAsync();
    }

    public async Task<bool> TryCancelAsync(Ulid id)
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

    private static JobInfo ToJobInfo(Job job, IReadOnlyList<ArtifactInfo>? artifacts = null, string? workerName = null, string? pipelineName = null) => new()
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
        PipelineName = pipelineName
    };
}
