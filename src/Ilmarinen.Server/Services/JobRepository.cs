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
    private readonly PriorityRepository _priorities;

    public JobRepository(IlmarinenDbContext db, ArtifactRepository artifacts, CredentialEncryptionService encryption, PriorityRepository priorities)
    {
        _db = db;
        _artifacts = artifacts;
        _encryption = encryption;
        _priorities = priorities;
    }

    public async Task<JobInfo> CreateAsync(JobSubmission submission)
    {
        // Resolve fields from pipeline if PipelineId is set
        string? resolvedRepoUrl = submission.RepoUrl;
        string? resolvedRef = submission.Ref;
        string? resolvedScriptPath = submission.ScriptPath;
        string? resolvedGitToken = null;
        Pipeline? pipeline = null;

        if (submission.PipelineId != null)
        {
            pipeline = await _db.Pipelines
                .Include(p => p.Repository)
                .FirstOrDefaultAsync(p => p.Id == submission.PipelineId.Value);

            if (pipeline == null)
                throw new InvalidSubmissionException($"Pipeline {submission.PipelineId} not found");

            resolvedRepoUrl ??= pipeline.Repository?.RepoUrl;
            resolvedRef ??= pipeline.DefaultRef;
            resolvedScriptPath ??= pipeline.ScriptPath;

            if (submission.GitTokenMode == GitTokenMode.Inherit)
                resolvedGitToken = _encryption.Decrypt(pipeline.Repository?.EncryptedGitToken);
        }

        if (submission.GitTokenMode == GitTokenMode.Explicit)
            resolvedGitToken = submission.GitToken;

        if (string.IsNullOrEmpty(resolvedRepoUrl))
            throw new InvalidSubmissionException("RepoUrl is required (either directly or via PipelineId)");
        if (string.IsNullOrEmpty(resolvedRef))
            throw new InvalidSubmissionException("Ref is required (either directly or via PipelineId)");
        if (string.IsNullOrEmpty(resolvedScriptPath))
            throw new InvalidSubmissionException("ScriptPath is required (either directly or via PipelineId)");

        // Each priority is the submission's if it names one, else the pipeline's, else the job default or no minimum.
        var priorityId = submission.Priority != null
            ? (await _priorities.ResolveAsync(PriorityKind.Job, submission.Priority)).Id
            : pipeline?.PriorityId ?? (await _priorities.GetDefaultAsync(PriorityKind.Job)).Id;
        var minWorkerPriorityId = submission.MinWorkerPriority != null
            ? (await _priorities.ResolveMinimumAsync(submission.MinWorkerPriority))?.Id
            : pipeline?.MinWorkerPriorityId;

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
            PipelineId = submission.PipelineId,
            MinWorkerPriorityId = minWorkerPriorityId,
            PriorityId = priorityId
        };

        _db.Jobs.Add(job);
        await PriorityRepository.WritingReferencesAsync(() => _db.SaveChangesAsync());
        await _db.Entry(job).Reference(j => j.Priority).LoadAsync();
        await _db.Entry(job).Reference(j => j.MinWorkerPriority).LoadAsync();

        return ToJobInfo(job);
    }

    public async Task<JobInfo?> GetAsync(Guid id)
    {
        var job = await WithPriorities().FirstOrDefaultAsync(j => j.Id == id);
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

    /// <summary>
    /// What a worker needs to run this job, git token decrypted.
    /// </summary>
    public async Task<JobAssignment> GetAssignmentAsync(Guid id, Guid assignmentId)
    {
        var job = await _db.Jobs.FirstAsync(j => j.Id == id);
        return new JobAssignment
        {
            Id = job.Id,
            AssignmentId = assignmentId,
            RepoUrl = job.RepoUrl,
            Ref = job.Ref,
            ScriptPath = job.ScriptPath,
            GitToken = _encryption.Decrypt(job.EncryptedGitToken)
        };
    }

    /// <summary>
    /// Creates a new job by retrying a completed (failed/cancelled) job.
    /// Returns null if the job doesn't exist or isn't in a terminal state.
    /// Throws if retry is blocked (e.g. explicit token was cleared).
    /// </summary>
    public async Task<JobSubmission?> BuildRetrySubmissionAsync(Guid id)
    {
        var job = await WithPriorities().FirstOrDefaultAsync(j => j.Id == id);
        if (job == null) return null;

        if (job.Status is not (JobStatus.Failed or JobStatus.Cancelled))
            return null;

        // By name, as any submission names them; a job with no minimum asks for none, so it doesn't inherit one its pipeline has gained since.
        var priority = job.Priority!.Name;
        var minWorkerPriority = job.MinWorkerPriority?.Name ?? PriorityNames.AnyWorker;

        switch (job.GitTokenMode)
        {
            case GitTokenMode.None:
                return new JobSubmission
                {
                    PipelineId = job.PipelineId,
                    RepoUrl = job.RepoUrl,
                    Ref = job.Ref,
                    ScriptPath = job.ScriptPath,
                    GitTokenMode = GitTokenMode.None,
                    MinWorkerPriority = minWorkerPriority,
                    Priority = priority
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
                    GitTokenMode = GitTokenMode.Inherit,
                    MinWorkerPriority = minWorkerPriority,
                    Priority = priority
                };
            }

            case GitTokenMode.Explicit:
                throw new InvalidOperationException("Cannot retry: explicit git token was cleared after job completion");

            default:
                throw new InvalidOperationException($"Unknown GitTokenMode: {job.GitTokenMode}");
        }
    }

    /// <summary>
    /// Applies a status transition the server decides on its own, honoring finality rules. Returns false when the write was refused (job missing, already final, or Cancelled and not being overridden). Running goes through <see cref="AssignAsync"/>, and a worker's report through <see cref="CompleteReportedAsync"/>.
    /// </summary>
    public async Task<bool> UpdateStatusAsync(Guid id, JobStatus status)
    {
        if (status == JobStatus.Running)
        {
            throw new ArgumentException("A job becomes Running only by being assigned", nameof(status));
        }

        var now = DateTime.UtcNow;

        // One conditional UPDATE so the finality check and the write are atomic — completion paths gate their side effects on the returned bool, and a check-then-write here would let a concurrent cancel be overwritten by Running or let two completion paths both claim the terminal transition. Same idiom as TryCancelAsync below.
        var eligible = _db.Jobs
            .Where(j => j.Id == id)
            // Success and Failed are truly final — nothing overrides them.
            .Where(j => j.Status != JobStatus.Success && j.Status != JobStatus.Failed)
            // Cancelled can be overridden by Success or Failed: if the worker actually completed the job before the cancel reached it, the real outcome wins.
            .Where(j => j.Status != JobStatus.Cancelled || status == JobStatus.Success || status == JobStatus.Failed);

        int rows;
        if (status is JobStatus.Success or JobStatus.Failed or JobStatus.Cancelled)
        {
            rows = await eligible.ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, status)
                .SetProperty(j => j.CompletedAt, now)
                .SetProperty(j => j.EncryptedGitToken, (string?)null)); // Clear token after job completion
        }
        else
        {
            rows = await eligible.ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, status));
        }

        return rows > 0;
    }

    /// <summary>Hands a queued job to a worker process. Refused (false) unless the job is still Queued — a cancel may have got there first.</summary>
    public async Task<bool> AssignAsync(Guid id, Guid workerId, Guid assignmentId, Guid workerInstanceId)
    {
        var now = DateTime.UtcNow;
        var rows = await _db.Jobs
            .Where(j => j.Id == id && j.Status == JobStatus.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Running)
                .SetProperty(j => j.WorkerId, workerId)
                .SetProperty(j => j.StartedAt, now)
                .SetProperty(j => j.AssignmentId, assignmentId)
                .SetProperty(j => j.WorkerInstanceId, workerInstanceId));

        return rows > 0;
    }

    /// <summary>Puts a job back in the queue, unrun, if it is still Running under this assignment.</summary>
    public async Task<bool> RequeueAsync(Guid id, Guid workerId, Guid assignmentId)
    {
        var rows = await _db.Jobs
            .Where(j => j.Id == id && j.Status == JobStatus.Running && j.WorkerId == workerId && j.AssignmentId == assignmentId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Queued)
                .SetProperty(j => j.WorkerId, (Guid?)null)
                .SetProperty(j => j.StartedAt, (DateTime?)null)
                .SetProperty(j => j.AssignmentId, (Guid?)null)
                .SetProperty(j => j.WorkerInstanceId, (Guid?)null));

        return rows > 0;
    }

    /// <summary>
    /// Records a worker's report of how its run of an assignment went: only for that assignment, and with the same finality rules as <see cref="UpdateStatusAsync"/> (so a real outcome still overrides a cancel). A Failed that takes effect also marks the worker as owing a diagnostic, in the same transaction, so a crash between the two can't skip the re-check. Returns whether the status changed.
    /// </summary>
    public async Task<bool> CompleteReportedAsync(Guid id, Guid workerId, Guid assignmentId, JobStatus status, JobDiskUse? diskUse)
    {
        if (status is not (JobStatus.Success or JobStatus.Failed or JobStatus.Cancelled))
        {
            throw new ArgumentException($"{status} isn't an outcome", nameof(status));
        }

        var now = DateTime.UtcNow;
        await using var transaction = await _db.Database.BeginTransactionAsync();

        var rows = await _db.Jobs
            .Where(j => j.Id == id && j.WorkerId == workerId && j.AssignmentId == assignmentId)
            .Where(j => j.Status == JobStatus.Running || (j.Status == JobStatus.Cancelled && status != JobStatus.Cancelled))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, status)
                .SetProperty(j => j.CompletedAt, now)
                .SetProperty(j => j.EncryptedGitToken, (string?)null)
                .SetProperty(j => j.WorkspaceKind, diskUse != null ? diskUse.WorkspaceKind : null)
                .SetProperty(j => j.WorkspaceName, diskUse != null ? diskUse.WorkspaceName : null)
                .SetProperty(j => j.WorkspaceFresh, diskUse != null ? diskUse.WorkspaceFresh : null)
                .SetProperty(j => j.DiskUsedBytes, diskUse != null ? diskUse.UsedBytes : null)
                .SetProperty(j => j.StoppedForDisk, diskUse != null ? diskUse.StoppedForDisk : null));

        // A job stopped for disk failed for want of space, which a host re-check wouldn't find wrong.
        if (rows > 0 && status == JobStatus.Failed && diskUse?.StoppedForDisk != true)
        {
            await _db.Workers
                .Where(w => w.Id == workerId)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.DiagnosticOwed, true));
        }

        await transaction.CommitAsync();
        return rows > 0;
    }

    /// <summary>Fails a job as lost with its worker's process, only if it is still Running for that worker.</summary>
    public async Task<bool> FailLostAsync(Guid id, Guid workerId)
    {
        var now = DateTime.UtcNow;
        var rows = await _db.Jobs
            .Where(j => j.Id == id && j.Status == JobStatus.Running && j.WorkerId == workerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Failed)
                .SetProperty(j => j.CompletedAt, now)
                .SetProperty(j => j.EncryptedGitToken, (string?)null));

        return rows > 0;
    }

    /// <summary>The Running and Queued jobs, the Queued ones in dispatch order (<see cref="InDispatchOrder"/>).</summary>
    public async Task<IReadOnlyList<UnfinishedJob>> GetUnfinishedJobsAsync()
    {
        return await InDispatchOrder(_db.Jobs.Where(j => j.Status == JobStatus.Running || j.Status == JobStatus.Queued))
            .Select(j => new UnfinishedJob(j.Status, j.WorkerId, j.RepoUrl, j.ScriptPath, j.StartedAt, j.MinWorkerPriority != null ? j.MinWorkerPriority.Rank : null))
            .ToListAsync();
    }

    /// <summary>The queued jobs, in dispatch order, with the rank of each one's minimum worker priority (null for none) and when it was submitted. Ranks are read here, so a reorder of the priorities counts from the next dispatch on.</summary>
    public async Task<IReadOnlyList<QueuedJobRow>> GetQueuedJobsAsync()
    {
        return await InDispatchOrder(_db.Jobs.Where(j => j.Status == JobStatus.Queued))
            .Select(j => new QueuedJobRow(j.Id, j.MinWorkerPriority != null ? j.MinWorkerPriority.Rank : null, j.CreatedAt, j.RepoUrl, j.ScriptPath))
            .ToListAsync();
    }

    /// <summary>How many of a script's latest runs disk estimates look at.</summary>
    public const int DiskHistorySize = 5;

    /// <summary>
    /// For each of these scripts, its latest <see cref="DiskHistorySize"/> finished runs whose worker reported what they worked in, and as many of its latest fresh runs besides (a persistent workspace's only fresh run is often its very first), newest first; a script with none is left out. Runs that ended before their script said what it works in (WorkspaceKind.Unknown) are left out: a broken commit says nothing about the next run's workspace or disk. Two queries however many scripts.
    /// </summary>
    public async Task<IReadOnlyDictionary<(string RepoUrl, string ScriptPath), IReadOnlyList<DiskRun>>> GetDiskHistoryAsync(IEnumerable<(string RepoUrl, string ScriptPath)> scripts)
    {
        var wanted = scripts.ToHashSet();
        if (wanted.Count == 0)
        {
            return new Dictionary<(string, string), IReadOnlyList<DiskRun>>();
        }

        // Narrowed by repositories and script paths separately (EF can't match pairs); a pair nobody asked for is dropped below.
        var repoUrls = wanted.Select(w => w.RepoUrl).Distinct().ToList();
        var scriptPaths = wanted.Select(w => w.ScriptPath).Distinct().ToList();
        var reported = _db.Jobs.Where(j => j.WorkspaceKind != null
            && j.WorkspaceKind != WorkspaceKind.Unknown
            && j.CompletedAt != null
            && repoUrls.Contains(j.RepoUrl)
            && scriptPaths.Contains(j.ScriptPath));

        var fresh = reported.Where(j => j.WorkspaceFresh == true || j.WorkspaceKind == WorkspaceKind.Ephemeral);
        var latest = await Latest(reported);
        var latestFresh = await Latest(fresh);

        return latest.Concat(latestFresh)
            .DistinctBy(r => r.Id)
            .GroupBy(r => (r.RepoUrl, r.ScriptPath))
            .Where(g => wanted.Contains(g.Key))
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<DiskRun>)g.OrderByDescending(r => r.CompletedAt)
                    .Select(r => new DiskRun(r.Kind, r.WorkspaceName, r.WorkspaceFresh ?? false, r.DiskUsedBytes, r.StoppedForDisk ?? false, r.Status == JobStatus.Success))
                    .ToList());

        // Per script, the latest few of these, as a lateral join.
        static async Task<List<DiskHistoryRow>> Latest(IQueryable<Job> runs)
        {
            return await runs
                .Select(j => new { j.RepoUrl, j.ScriptPath })
                .Distinct()
                .SelectMany(k => runs
                    .Where(j => j.RepoUrl == k.RepoUrl && j.ScriptPath == k.ScriptPath)
                    .OrderByDescending(j => j.CompletedAt)
                    .Take(DiskHistorySize))
                .Select(j => new DiskHistoryRow(j.Id, j.RepoUrl, j.ScriptPath, j.CompletedAt, j.WorkspaceKind!.Value, j.WorkspaceName, j.WorkspaceFresh, j.DiskUsedBytes, j.StoppedForDisk, j.Status))
                .ToListAsync();
        }
    }

    private sealed record DiskHistoryRow(Guid Id, string RepoUrl, string ScriptPath, DateTime? CompletedAt, WorkspaceKind Kind, string? WorkspaceName, bool? WorkspaceFresh, long? DiskUsedBytes, bool? StoppedForDisk, JobStatus Status);

    /// <summary>Every job Running, oldest first, with the name of the worker running it.</summary>
    public async Task<IReadOnlyList<DrainRunningJob>> GetRunningWithWorkersAsync()
    {
        return await _db.Jobs
            .Where(j => j.Status == JobStatus.Running)
            .OrderBy(j => j.CreatedAt)
            .Select(j => new DrainRunningJob(j.Id, _db.Workers.Where(w => w.Id == j.WorkerId).Select(w => w.Name).FirstOrDefault()))
            .ToListAsync();
    }

    /// <summary>The jobs Running for a worker, with the assignment each is running under.</summary>
    public async Task<IReadOnlyList<RunningAssignment>> GetRunningAssignmentsAsync(Guid workerId)
    {
        return await _db.Jobs
            .Where(j => j.WorkerId == workerId && j.Status == JobStatus.Running)
            .Select(j => new RunningAssignment(j.Id, j.AssignmentId, j.WorkerInstanceId))
            .ToListAsync();
    }

    public async Task SetCommitAsync(Guid id, string commitSha)
    {
        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.Id == id);
        if (job == null) return;

        job.Commit = commitSha;
        await _db.SaveChangesAsync();
    }

    /// <summary>The worker a job was last assigned to, or null if it never was (or doesn't exist).</summary>
    public async Task<Guid?> GetAssignedWorkerAsync(Guid jobId)
    {
        return await _db.Jobs
            .Where(j => j.Id == jobId)
            .Select(j => j.WorkerId)
            .FirstOrDefaultAsync();
    }

    /// <summary>Every job, newest first, or only those of a job priority, or needing a worker priority.</summary>
    public async Task<IReadOnlyList<JobInfo>> GetAllAsync(Guid? priorityId = null, Guid? minWorkerPriorityId = null)
    {
        var workerNames = await _db.Workers.ToDictionaryAsync(w => w.Id, w => w.Name);
        var pipelineNames = await _db.Pipelines.ToDictionaryAsync(p => p.Id, p => p.Name);

        var jobs = await WithPriorities()
            .Where(j => priorityId == null || j.PriorityId == priorityId)
            .Where(j => minWorkerPriorityId == null || j.MinWorkerPriorityId == minWorkerPriorityId)
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

        var jobs = await WithPriorities()
            .Where(j => j.PipelineId == pipelineId)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync();

        var retryablePipelines = await GetRetryablePipelineIdsAsync();

        return jobs.Select(j => ToJobInfo(j,
            workerName: j.WorkerId != null && workerNames.TryGetValue(j.WorkerId.Value, out var wName) ? wName : null,
            pipelineName: pipelineName,
            retryablePipelines: retryablePipelines)).ToList();
    }

    /// <summary>
    /// The latest ten runs, plus the one left out of them. See <see cref="EstimateDuration"/>.
    /// </summary>
    private const int EstimateSampleSize = 11;

    /// <summary>
    /// Expected durations of the running jobs among <paramref name="jobs"/>, keyed by job ID, for those whose worker has run the same script of the same repository successfully more than once. Web UI only: JobInfo is a protocol type, and growing it would change the protocol hash for a display feature.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, TimeSpan>> GetDurationEstimatesAsync(IEnumerable<JobInfo> jobs)
    {
        var running = jobs.Where(j => j.Status == JobStatus.Running && j.WorkerId != null).ToList();
        var known = await GetScriptEstimatesAsync(running.Select(j => (j.RepoUrl, j.ScriptPath)));

        var estimates = new Dictionary<Guid, TimeSpan>();
        foreach (var job in running)
        {
            if (known.PerWorker.TryGetValue((job.WorkerId!.Value, job.RepoUrl, job.ScriptPath), out var estimate))
            {
                estimates[job.Id] = estimate;
            }
        }
        return estimates;
    }

    /// <summary>
    /// How long each worker takes to run each of these scripts (<see cref="EstimateDuration"/>), and — rougher, for a worker that hasn't — the median of the latest ten successful runs on any worker: workers differ, and across workers there is no one setup run to leave out. Two queries however many scripts and workers there are.
    /// </summary>
    public async Task<ScriptEstimates> GetScriptEstimatesAsync(IEnumerable<(string RepoUrl, string ScriptPath)> scripts)
    {
        var wanted = scripts.ToHashSet();
        var perWorker = new Dictionary<(Guid WorkerId, string RepoUrl, string ScriptPath), TimeSpan>();
        var acrossWorkers = new Dictionary<(string RepoUrl, string ScriptPath), TimeSpan>();
        if (wanted.Count == 0)
        {
            return new ScriptEstimates(perWorker, acrossWorkers);
        }

        // Narrowed by repositories and script paths separately (EF can't match pairs); a pair nobody asked for is dropped below.
        var repoUrls = wanted.Select(w => w.RepoUrl).Distinct().ToList();
        var scriptPaths = wanted.Select(w => w.ScriptPath).Distinct().ToList();
        var successes = _db.Jobs.Where(j => j.Status == JobStatus.Success
            && j.StartedAt != null
            && j.CompletedAt != null
            && repoUrls.Contains(j.RepoUrl)
            && scriptPaths.Contains(j.ScriptPath));

        // Per key, the latest few: a lateral join, which the history index answers without reading every success.
        var byWorker = await successes
            .Where(j => j.WorkerId != null)
            .Select(j => new { j.WorkerId, j.RepoUrl, j.ScriptPath })
            .Distinct()
            .SelectMany(k => successes
                .Where(j => j.WorkerId == k.WorkerId && j.RepoUrl == k.RepoUrl && j.ScriptPath == k.ScriptPath)
                .OrderByDescending(j => j.CompletedAt)
                .Take(EstimateSampleSize))
            .Select(j => new { WorkerId = j.WorkerId!.Value, j.RepoUrl, j.ScriptPath, StartedAt = j.StartedAt!.Value, CompletedAt = j.CompletedAt!.Value })
            .ToListAsync();
        foreach (var triple in byWorker.GroupBy(j => (j.WorkerId, j.RepoUrl, j.ScriptPath)).Where(g => wanted.Contains((g.Key.RepoUrl, g.Key.ScriptPath))))
        {
            var durations = triple.OrderByDescending(j => j.CompletedAt).Select(j => j.CompletedAt - j.StartedAt).ToList();
            if (EstimateDuration(durations) is { } estimate)
            {
                perWorker[triple.Key] = estimate;
            }
        }

        var byScript = await successes
            .Select(j => new { j.RepoUrl, j.ScriptPath })
            .Distinct()
            .SelectMany(k => successes
                .Where(j => j.RepoUrl == k.RepoUrl && j.ScriptPath == k.ScriptPath)
                .OrderByDescending(j => j.CompletedAt)
                .Take(EstimateSampleSize - 1))
            .Select(j => new { j.RepoUrl, j.ScriptPath, StartedAt = j.StartedAt!.Value, CompletedAt = j.CompletedAt!.Value })
            .ToListAsync();
        foreach (var pair in byScript.GroupBy(j => (j.RepoUrl, j.ScriptPath)).Where(g => wanted.Contains(g.Key)))
        {
            if (Median(pair.Select(j => j.CompletedAt - j.StartedAt).ToList()) is { } estimate)
            {
                acrossWorkers[pair.Key] = estimate;
            }
        }

        return new ScriptEstimates(perWorker, acrossWorkers);
    }

    /// <summary>
    /// The median of the latest ten successful runs, leaving out the very first one: a script's first successful run on a worker does its setup (mirror, workspace, images) and says little about the rest. Null when that leaves nothing. Only the latest <see cref="EstimateSampleSize"/> are looked at and the oldest of those is dropped, which while history is short is that first run, and otherwise the one just beyond the latest ten.
    /// </summary>
    public static TimeSpan? EstimateDuration(IReadOnlyList<TimeSpan> successesNewestFirst)
    {
        return Median(successesNewestFirst.Take(EstimateSampleSize).SkipLast(1).ToList());
    }

    private static TimeSpan? Median(IReadOnlyList<TimeSpan> durations)
    {
        var sample = durations.Order().ToList();
        if (sample.Count == 0)
        {
            return null;
        }

        var middle = sample.Count / 2;
        if (sample.Count % 2 == 1)
        {
            return sample[middle];
        }
        return (sample[middle - 1] + sample[middle]) / 2;
    }

    public async Task<IReadOnlyList<JobInfo>> GetByWorkerAsync(Guid workerId)
    {
        var workerName = await _db.Workers.Where(w => w.Id == workerId).Select(w => w.Name).FirstOrDefaultAsync();
        var pipelineNames = await _db.Pipelines.ToDictionaryAsync(p => p.Id, p => p.Name);

        var jobs = await WithPriorities()
            .Where(j => j.WorkerId == workerId)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync();

        var retryablePipelines = await GetRetryablePipelineIdsAsync();

        return jobs.Select(j => ToJobInfo(j,
            workerName: workerName,
            pipelineName: j.PipelineId != null && pipelineNames.TryGetValue(j.PipelineId.Value, out var pName) ? pName : null,
            retryablePipelines: retryablePipelines)).ToList();
    }

    /// <summary>The order dispatch takes queued jobs in: highest priority first, oldest first among equals. The queue forecast reads the queue in this same order, so it simulates the dispatch that really happens.</summary>
    private static IQueryable<Job> InDispatchOrder(IQueryable<Job> jobs)
    {
        return jobs
            .OrderByDescending(j => j.Priority!.Rank)
            .ThenBy(j => j.CreatedAt)
            .ThenBy(j => j.Id);
    }

    /// <summary>
    /// Queued jobs whose minimum is above the highest available worker's priority, oldest first — every queued job when no worker is available. A job with no minimum is never one of them while some worker is.
    /// </summary>
    public async Task<IReadOnlyList<JobInfo>> GetUnsatisfiableJobsAsync(int? highestAvailableRank)
    {
        var queued = WithPriorities().Where(j => j.Status == JobStatus.Queued);
        if (highestAvailableRank is { } highest)
        {
            queued = queued.Where(j => j.MinWorkerPriority != null && j.MinWorkerPriority.Rank > highest);
        }

        var jobs = await queued
            .OrderBy(j => j.CreatedAt)
            .ThenBy(j => j.Id)
            .ToListAsync();

        // Polled on a timer and almost always empty, so skip resolving pipeline names when there is nothing to name.
        if (jobs.Count == 0)
        {
            return [];
        }

        var pipelineIds = jobs.Where(j => j.PipelineId != null).Select(j => j.PipelineId!.Value).Distinct().ToList();
        var pipelineNames = await _db.Pipelines
            .Where(p => pipelineIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name);

        return jobs.Select(j => ToJobInfo(j,
            pipelineName: j.PipelineId != null && pipelineNames.TryGetValue(j.PipelineId.Value, out var pName) ? pName : null)).ToList();
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

    /// <summary>Jobs read with their priorities, in the same statement: a priority deleted between two reads would leave a job naming one that is gone.</summary>
    private IQueryable<Job> WithPriorities()
    {
        return _db.Jobs.Include(j => j.Priority).Include(j => j.MinWorkerPriority);
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
            MinWorkerPriority = job.MinWorkerPriority is { } minimum ? PriorityRepository.ToRef(minimum) : null,
            Priority = PriorityRepository.ToRef(job.Priority ?? throw new InvalidOperationException("Job loaded without its priority")),
            CanRetry = canRetry,
            RetryBlockedReason = retryBlockedReason,
            DiskUse = job.WorkspaceKind is { } kind
                ? new JobDiskUse { WorkspaceKind = kind, WorkspaceName = job.WorkspaceName, WorkspaceFresh = job.WorkspaceFresh ?? false, UsedBytes = job.DiskUsedBytes, StoppedForDisk = job.StoppedForDisk ?? false }
                : null
        };
    }
}

public record RunningAssignment(Guid JobId, Guid? AssignmentId, Guid? WorkerInstanceId);

/// <summary>A Queued job as dispatch and waking read it; MinWorkerRank is the rank of its minimum worker priority, null for none.</summary>
public record QueuedJobRow(Guid Id, int? MinWorkerRank, DateTime CreatedAt, string RepoUrl, string ScriptPath);

public record ScriptEstimates(IReadOnlyDictionary<(Guid WorkerId, string RepoUrl, string ScriptPath), TimeSpan> PerWorker, IReadOnlyDictionary<(string RepoUrl, string ScriptPath), TimeSpan> AcrossWorkers);

/// <summary>A Running or Queued job as the queue forecast reads it; MinWorkerRank is the rank of its minimum worker priority, null for none.</summary>
public record UnfinishedJob(JobStatus Status, Guid? WorkerId, string RepoUrl, string ScriptPath, DateTime? StartedAt, int? MinWorkerRank);
