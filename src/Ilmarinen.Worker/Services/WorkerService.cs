using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Microsoft.AspNetCore.SignalR.Client;

namespace Ilmarinen.Worker.Services;

public class WorkerService : BackgroundService
{
    private readonly WorkerConfig _config;
    private readonly ILogger<WorkerService> _logger;
    private HubConnection? _connection;

    public WorkerService(WorkerConfig config, ILogger<WorkerService> logger)
    {
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_config.WorkspacePath);

        _connection = new HubConnectionBuilder()
            .WithUrl($"{_config.ServerUrl}/workers")
            .WithAutomaticReconnect()
            .Build();

        _connection.On<JobAssignment>("AssignJob", OnJobAssigned);

        _connection.Reconnecting += _ =>
        {
            _logger.LogWarning("Connection lost, attempting to reconnect...");
            return Task.CompletedTask;
        };

        _connection.Reconnected += async _ =>
        {
            _logger.LogInformation("Reconnected to server, re-registering...");
            await RegisterAndReady();
        };

        await ConnectWithRetryAsync(stoppingToken);
        await RegisterAndReady();

        _logger.LogInformation("Worker {WorkerId} connected and ready", _config.WorkerId);

        // Heartbeat loop
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                if (_connection.State == HubConnectionState.Connected)
                {
                    await _connection.SendAsync("Heartbeat", new WorkerHeartbeat
                    {
                        WorkerId = _config.WorkerId,
                        IsReady = true
                    }, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Heartbeat failed");
            }
        }
    }

    private async Task ConnectWithRetryAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Connecting to {ServerUrl}...", _config.ServerUrl);
                await _connection!.StartAsync(ct);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect, retrying in 5 seconds...");
                await Task.Delay(5000, ct);
            }
        }
    }

    private async Task RegisterAndReady()
    {
        await _connection!.SendAsync("Register", new WorkerRegister
        {
            WorkerId = _config.WorkerId
        });

        await _connection.SendAsync("Ready");
    }

    private async Task OnJobAssigned(JobAssignment job)
    {
        _logger.LogInformation("Received job {JobId}: {RepoUrl} @ {Ref}", job.Id, job.RepoUrl, job.Ref);

        var startTime = DateTime.UtcNow;

        try
        {
            await _connection!.SendAsync("JobStarted", job.Id);

            var runner = new JobRunner(_config, job, _connection, _logger);
            var result = await runner.ExecuteAsync();

            result = result with { Duration = DateTime.UtcNow - startTime };

            await _connection.SendAsync("JobCompleted", job.Id, result);
            _logger.LogInformation("Job {JobId} completed with status {Status}", job.Id, result.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} failed with exception", job.Id);

            await _connection!.SendAsync("JobCompleted", job.Id, new JobCompleted
            {
                Id = job.Id,
                Status = JobStatus.Failed,
                Duration = DateTime.UtcNow - startTime
            });
        }

        // Signal ready for next job
        await _connection!.SendAsync("Ready");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_connection != null)
        {
            await _connection.StopAsync(cancellationToken);
            await _connection.DisposeAsync();
        }

        await base.StopAsync(cancellationToken);
    }
}
