using System.Collections.Concurrent;
using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using NUlid;

namespace Ilmarinen.Server.Services;

public class JobScheduler
{
    private readonly ConcurrentQueue<Ulid> _pendingJobs = new();
    private readonly JobRepository _jobs;
    private readonly WorkerRepository _workers;
    private readonly IHubContext<WorkerHub, IWorkerClient> _hubContext;

    public JobScheduler(
        JobRepository jobs,
        WorkerRepository workers,
        IHubContext<WorkerHub, IWorkerClient> hubContext)
    {
        _jobs = jobs;
        _workers = workers;
        _hubContext = hubContext;
    }

    public async Task<Ulid> EnqueueJobAsync(JobSubmission submission)
    {
        var info = await _jobs.CreateAsync(submission);
        await _jobs.UpdateStatusAsync(info.Id, JobStatus.Queued);
        _pendingJobs.Enqueue(info.Id);
        return info.Id;
    }

    public async Task<bool> TryAssignJobAsync(string connectionId)
    {
        var worker = await _workers.GetByConnectionIdAsync(connectionId);
        if (worker == null || !worker.IsReady)
            return false;

        if (!_pendingJobs.TryDequeue(out var jobId))
            return false;

        var submission = await _jobs.GetSubmissionAsync(jobId);
        if (submission == null)
            return false;

        var assignment = new JobAssignment
        {
            Id = jobId,
            RepoUrl = submission.RepoUrl,
            Ref = submission.Ref,
            ScriptPath = submission.ScriptPath
        };

        await _jobs.UpdateStatusAsync(jobId, JobStatus.Running, worker.Id);
        await _workers.SetCurrentJobAsync(connectionId, jobId);

        await _hubContext.Clients.Client(connectionId).AssignJob(assignment);
        return true;
    }

    public async Task CompleteJobAsync(Ulid jobId, JobCompleted result)
    {
        await _jobs.UpdateStatusAsync(jobId, result.Status);
    }

    public async Task<bool> CancelJobAsync(Ulid jobId)
    {
        return await _jobs.TryCancelAsync(jobId);
    }
}
