using Ilmarinen.Worker.Presence;
using Ilmarinen.Worker.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.Worker.Host;

/// <summary>
/// Tells the server before the host sleeps, and reconnects as soon as it wakes. Without it, a sleeping worker's connection looks alive to the server until its timeout, and a job dispatched in that window sits Running on a machine that is asleep. A logind delay inhibitor holds the suspend back the moment it takes to say so.
/// </summary>
public sealed class SleepWatcher : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private readonly Logind _logind;
    private readonly WorkerService _worker;
    private readonly PresenceService _presence;
    private readonly ILogger<SleepWatcher> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private SafeFileHandle? _delay;
    private bool _failureReported;
    private CancellationToken _stoppingToken;

    public SleepWatcher(Logind logind, WorkerService worker, PresenceService presence, ILogger<SleepWatcher> logger)
    {
        _logind = logind;
        _worker = worker;
        _presence = presence;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var ended = await _logind.WatchPrepareForSleepAsync(OnPrepareForSleep, ex => _logger.LogWarning(ex, "Watching for the host going to sleep failed"), stoppingToken);
                await TakeDelayAsync();
                _logger.LogInformation("Watching for the host going to sleep");
                _failureReported = false;
                var reason = await ended.WaitAsync(stoppingToken);
                _logger.LogWarning(reason, "Lost the connection to logind; no longer watching for the host going to sleep");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Information, as SleepInhibitor's: plenty of hosts have no bus to watch and never sleep.
                if (!_failureReported)
                {
                    _failureReported = true;
                    _logger.LogInformation(ex, "Not watching for the host going to sleep (mount the host's D-Bus socket, see docker-compose.inhibit-sleep.yml)");
                }
                else
                {
                    _logger.LogDebug(ex, "Still not watching for the host going to sleep");
                }
            }

            ReleaseDelay();
            try
            {
                await Task.Delay(RetryDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        ReleaseDelay();
    }

    private void OnPrepareForSleep(bool sleeping)
    {
        _ = HandleAsync(sleeping);
    }

    private async Task HandleAsync(bool sleeping)
    {
        await _lock.WaitAsync();
        try
        {
            if (sleeping)
            {
                _logger.LogInformation("The host is going to sleep: telling the server and closing the connection");
                // Sleeping first: clearing in-use before it would show the server an idle worker with room for a job, for a moment, on a machine that is going to sleep.
                await _worker.PrepareForSleepAsync();
                await _presence.MachineSleepingAsync();
                ReleaseDelay();
            }
            else
            {
                _logger.LogInformation("The host has woken: reconnecting");
                await _worker.WokeAsync();
                _presence.MachineWoke();
                await TakeDelayAsync();
            }
        }
        catch (Exception ex)
        {
            // Start over from a new connection, which takes the delay inhibitor afresh: without it every later sleep would go unannounced.
            _logger.LogError(ex, "Handling the host {Event} failed; reconnecting to logind", sleeping ? "going to sleep" : "waking");
            ReleaseDelay();
            _logind.Reset();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task TakeDelayAsync()
    {
        if (_delay == null)
        {
            _delay = await _logind.InhibitAsync("sleep", PresenceRule.OwnInhibitorWho, "Telling the Ilmarinen server before the host sleeps", "delay", _stoppingToken);
        }
    }

    private void ReleaseDelay()
    {
        _delay?.Dispose();
        _delay = null;
    }
}
