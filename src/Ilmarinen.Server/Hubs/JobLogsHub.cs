using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Server.Hubs;

public class JobLogsHub : Hub<IJobLogsClient>
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JobLogsHub> _logger;

    public JobLogsHub(IServiceScopeFactory scopeFactory, ILogger<JobLogsHub> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// UI client subscribes to logs for a specific job.
    /// Sends historical logs then joins live group.
    /// </summary>
    public async Task Subscribe(Guid jobId, int? fromSequence = null)
    {
        var groupName = $"job-logs-{jobId}";
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);

        // Send historical chunks the client doesn't have
        using var scope = _scopeFactory.CreateScope();
        var logRepo = scope.ServiceProvider.GetRequiredService<JobLogRepository>();

        var startSequence = fromSequence ?? 0;
        var chunks = await logRepo.GetChunksAsync(jobId, startSequence, limit: 100);

        foreach (var chunk in chunks)
        {
            await Clients.Caller.ReceiveLogChunk(new LogBroadcast
            {
                JobId = jobId,
                SequenceNumber = chunk.SequenceNumber,
                Content = chunk.Content,
                Timestamp = chunk.Timestamp
            });
        }

        _logger.LogDebug(
            "Client {ConnectionId} subscribed to job {JobId} logs from sequence {Seq}",
            Context.ConnectionId, jobId, startSequence);
    }

    /// <summary>
    /// UI client unsubscribes from job logs.
    /// </summary>
    public async Task Unsubscribe(Guid jobId)
    {
        var groupName = $"job-logs-{jobId}";
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Groups are automatically cleaned up by SignalR
        await base.OnDisconnectedAsync(exception);
    }
}
