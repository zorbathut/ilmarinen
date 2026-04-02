using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Server.Services;

public class PipelineSchedulerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly JobScheduler _scheduler;
    private readonly ILogger<PipelineSchedulerService> _logger;

    public PipelineSchedulerService(
        IServiceScopeFactory scopeFactory,
        JobScheduler scheduler,
        ILogger<PipelineSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _scheduler = scheduler;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial check on startup for any overdue schedules
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckSchedulesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking pipeline schedules");
            }

            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
    }

    private async Task CheckSchedulesAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var pipelines = scope.ServiceProvider.GetRequiredService<PipelineRepository>();
        var now = DateTime.UtcNow;

        var scheduled = await pipelines.GetScheduledPipelinesAsync();

        foreach (var pipeline in scheduled)
        {
            try
            {
                if (!CronValidator.TryParse(pipeline.Schedule, out var cron))
                {
                    _logger.LogWarning("Pipeline {PipelineId} ({Name}) has invalid cron expression: {Schedule}",
                        pipeline.Id, pipeline.Name, pipeline.Schedule);
                    continue;
                }

                var baseline = pipeline.LastTriggeredAt ?? pipeline.CreatedAt;
                var nextOccurrence = cron!.GetNextOccurrence(baseline, inclusive: false);

                if (nextOccurrence == null || nextOccurrence > now)
                    continue;

                _logger.LogInformation("Triggering scheduled pipeline {PipelineId} ({Name}), schedule: {Schedule}",
                    pipeline.Id, pipeline.Name, pipeline.Schedule);

                await pipelines.UpdateLastTriggeredAtAsync(pipeline.Id, now);
                await _scheduler.EnqueueJobAsync(new JobSubmission
                {
                    PipelineId = pipeline.Id,
                    GitTokenMode = GitTokenMode.Inherit
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error triggering scheduled pipeline {PipelineId} ({Name})",
                    pipeline.Id, pipeline.Name);
            }
        }
    }
}
