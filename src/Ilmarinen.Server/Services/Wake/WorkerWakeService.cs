using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.Server.Services.Wake;

/// <summary>Runs a round of waking every few seconds, while waking is on. Only on this timer, never on dispatch: a job unassigned for a moment, as around a reconnect, shouldn't wake anyone.</summary>
public sealed class WorkerWakeService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private readonly WorkerWaker _waker;
    private readonly TimeProvider _time;
    private readonly ILogger<WorkerWakeService> _logger;

    public WorkerWakeService(WorkerWaker waker, TimeProvider time, ILogger<WorkerWakeService> logger)
    {
        _waker = waker;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_waker.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, _time, stoppingToken);
                await _waker.TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A round of waking sleeping workers failed");
            }
        }
    }
}
