using Ilmarinen.Database;
using Ilmarinen.Database.Entities;
using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Microsoft.EntityFrameworkCore;
using NUlid;

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

    public async Task<JobInfo> CreateAsync(JobSubmission submission, Ulid? pipelineId = null)
    {
        var job = new Job
        {
            Id = Ulid.NewUlid(),
            Status = JobStatus.Pending,
            RepoUrl = submission.RepoUrl,
            Ref = submission.Ref,
            ScriptPath = submission.ScriptPath,
            CreatedAt = DateTime.UtcNow,
            EncryptedGitToken = _encryption.Encrypt(submission.GitToken),
            PipelineId = pipelineId
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
            RepoUrl = job.RepoUrl,
            Ref = job.Ref,
            ScriptPath = job.ScriptPath,
            GitToken = _encryption.Decrypt(job.EncryptedGitToken)
        } : null;
    }

    public async Task UpdateStatusAsync(Ulid id, JobStatus status, Ulid? workerId = null)
    {
        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.Id == id);
        if (job != null)
        {
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
        ScriptPath = job.ScriptPath,
        WorkerId = job.WorkerId,
        WorkerName = workerName,
        CreatedAt = job.CreatedAt,
        StartedAt = job.StartedAt,
        CompletedAt = job.CompletedAt,
        Artifacts = artifacts,
        PipelineId = job.PipelineId,
        PipelineName = pipelineName
    };
}
