using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Threading;
using System;
namespace Ilmarinen.Server.Services;

public class NotificationCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NotificationCleanupService> _logger;

    public NotificationCleanupService(IServiceScopeFactory scopeFactory, ILogger<NotificationCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);

                using var scope = _scopeFactory.CreateScope();
                var subscribers = scope.ServiceProvider.GetRequiredService<SubscriberRepository>();
                var notifications = scope.ServiceProvider.GetRequiredService<NotificationRepository>();

                await subscribers.DeactivateStaleAsync();
                await notifications.UnlockStaleAsync();
                await notifications.DeleteOldProcessedAsync(TimeSpan.FromDays(7));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in notification cleanup");
            }
        }
    }
}
