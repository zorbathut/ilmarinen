using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NUlid;

namespace Ilmarinen.NotificationClient;

public class NotificationProcessor : BackgroundService
{
    private readonly IlmarinenNotificationClient _client;
    private readonly INotificationHandler _handler;
    private readonly NotificationProcessorOptions _options;
    private readonly ILogger<NotificationProcessor> _logger;

    public NotificationProcessor(
        IlmarinenNotificationClient client,
        INotificationHandler handler,
        NotificationProcessorOptions options,
        ILogger<NotificationProcessor> logger)
    {
        _client = client;
        _handler = handler;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Register as subscriber
        var maxRetries = 10;
        var retryDelay = TimeSpan.FromSeconds(5);

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            stoppingToken.ThrowIfCancellationRequested();
            try
            {
                var info = await _client.RegisterAsync(_options.SubscriberName, _options.HeartbeatTimeoutMinutes);
                _logger.LogInformation("Registered as subscriber {Name} (ID: {Id})", info.Name, info.Id);
                break;
            }
            catch (HttpRequestException ex) when (attempt < maxRetries)
            {
                _logger.LogWarning(ex,
                    "Failed to connect to server (attempt {Attempt}/{MaxRetries}). Retrying in {Delay}s...",
                    attempt, maxRetries, retryDelay.TotalSeconds);
                await Task.Delay(retryDelay, stoppingToken);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 60));
            }
        }

        // Start heartbeat loop
        var heartbeatTask = HeartbeatLoopAsync(stoppingToken);

        // Main polling loop
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var notifications = await _client.PullNotificationsAsync(_options.BatchSize);
                var acked = new List<Ulid>();

                foreach (var notification in notifications)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    try
                    {
                        var shouldAck = await _handler.HandleAsync(notification, stoppingToken);
                        if (shouldAck)
                            acked.Add(notification.NotificationId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to handle notification {Id}", notification.NotificationId);
                    }
                }

                if (acked.Count > 0)
                    await _client.AcknowledgeAsync(acked);

                if (notifications.Count < _options.BatchSize)
                    await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in notification polling loop");
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
        }

        await heartbeatTask;
    }

    private async Task HeartbeatLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.HeartbeatInterval, stoppingToken);
                await _client.HeartbeatAsync();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Heartbeat failed, will retry next interval");
            }
        }
    }
}
