using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Server.Services;
using Microsoft.AspNetCore.SignalR;
using NUlid;

namespace Ilmarinen.Server.Hubs;

public class WorkerHub : Hub<IWorkerClient>
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WorkerHub> _logger;

    public WorkerHub(IServiceScopeFactory scopeFactory, ILogger<WorkerHub> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task Register(WorkerRegister info)
    {
        using var scope = _scopeFactory.CreateScope();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

        var workerId = await workers.RegisterOrUpdateAsync(Context.ConnectionId, info);
        _logger.LogInformation("Worker registered: {WorkerId}", workerId);
    }

    public async Task Ready()
    {
        using var scope = _scopeFactory.CreateScope();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();
        var scheduler = scope.ServiceProvider.GetRequiredService<JobScheduler>();

        await workers.SetReadyAsync(Context.ConnectionId, true);
        _logger.LogInformation("Worker ready: {ConnectionId}", Context.ConnectionId);

        await scheduler.TryAssignJobAsync(Context.ConnectionId);
    }

    public async Task JobStarted(Ulid jobId)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();

        _logger.LogInformation("Job started: {JobId}", jobId);
        await jobs.UpdateStatusAsync(jobId, JobStatus.Running);
    }

    public async Task StreamLogs(LogChunk chunk)
    {
        var logService = _scopeFactory.CreateScope()
            .ServiceProvider.GetRequiredService<LogStreamService>();

        await logService.ProcessChunkAsync(chunk);
    }

    public async Task JobCompleted(Ulid jobId, JobCompleted result)
    {
        using var scope = _scopeFactory.CreateScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<JobScheduler>();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();
        var logService = scope.ServiceProvider.GetRequiredService<LogStreamService>();

        _logger.LogInformation("Job completed: {JobId} - {Status}", jobId, result.Status);
        await scheduler.CompleteJobAsync(jobId, result);
        await logService.NotifyJobCompletedAsync(jobId, result.Status);
        await workers.SetCurrentJobAsync(Context.ConnectionId, null);

        await scheduler.TryAssignJobAsync(Context.ConnectionId);
    }

    public async Task Heartbeat(WorkerHeartbeat status)
    {
        using var scope = _scopeFactory.CreateScope();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

        await workers.UpdateHeartbeatAsync(Context.ConnectionId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        using var scope = _scopeFactory.CreateScope();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
        var logService = scope.ServiceProvider.GetRequiredService<LogStreamService>();

        var worker = await workers.GetByConnectionIdAsync(Context.ConnectionId);
        if (worker != null)
        {
            _logger.LogInformation("Worker disconnected: {WorkerId}", worker.Id);

            if (worker.CurrentJobId.HasValue)
            {
                await logService.NotifyJobCompletedAsync(worker.CurrentJobId.Value, JobStatus.Failed);
                await jobs.UpdateStatusAsync(worker.CurrentJobId.Value, JobStatus.Failed);
            }
        }

        await workers.SetDisconnectedAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
