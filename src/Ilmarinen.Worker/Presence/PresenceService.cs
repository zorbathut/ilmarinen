using Ilmarinen.Protocol;
using Ilmarinen.Worker.Host;
using Ilmarinen.Worker.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.Worker.Presence;

/// <summary>
/// Holds the worker back from new jobs while a person is using its machine (<see cref="PresenceRule"/>). It runs only where the worker can read a keyboard or mouse, which a container can only once the in-use overlay mounts /dev/input (deploy/worker-docker/docker-compose.in-use.yml); the screen lock and other programs' inhibitors come from logind, if the host's bus is mounted too.
/// </summary>
public sealed class PresenceService : BackgroundService
{
    // Short, and judged afresh each time against the wall clock: a single long timer would stall across a suspend, which stops the clock it runs on.
    private static readonly TimeSpan JudgeInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RescanInterval = TimeSpan.FromMinutes(1);

    private readonly WorkerConfig _config;
    private readonly WorkerService _worker;
    private readonly Logind _logind;
    private readonly TimeProvider _time;
    private readonly ILogger<PresenceService> _logger;
    private readonly InputActivity _input;
    private readonly PresenceReport _report;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _inUse;
    private bool _logindReported;

    public PresenceService(WorkerConfig config, WorkerService worker, Logind logind, PresenceReport report, TimeProvider time, ILogger<PresenceService> logger)
    {
        _config = config;
        _worker = worker;
        _logind = logind;
        _report = report;
        _time = time;
        _logger = logger;
        _input = new InputActivity("/dev/input", InputDevices.ListPath, time, logger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _input.Rescan();
        if (_input.Reading.Count == 0)
        {
            _report.Record([], null);
            _logger.LogInformation("No keyboard or mouse is readable, so the worker won't hold back while someone uses this machine (see the in-use compose overlay)");
            return;
        }
        _logger.LogInformation("Holding back from new jobs while someone uses this machine, unless the server says to take them anyway: watching {Devices}, idle after {Minutes} minutes", string.Join(", ", _input.Reading.Select(d => $"{d.EventNode} ({d.Name})")), _config.InUseIdleTime.TotalMinutes);
        await JudgeAsync(stoppingToken);

        var lastRescan = _time.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(JudgeInterval, _time, stoppingToken);
                if (_time.GetUtcNow() - lastRescan >= RescanInterval)
                {
                    _input.Rescan();
                    lastRescan = _time.GetUtcNow();
                }
                await JudgeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Judging whether someone is using this machine failed");
            }
        }
    }

    /// <summary>The host is about to sleep. Whoever was using it has gone, or will have by the time it wakes: it counts as unattended until there is new input.</summary>
    public async Task MachineSleepingAsync()
    {
        await _lock.WaitAsync();
        try
        {
            _input.Forget();
            if (_inUse)
            {
                _inUse = false;
                _logger.LogInformation("Going to sleep: whoever was using this machine counts as gone until there is new input");
                await _worker.SetHoldAsync(WorkerHold.InUse, false);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The host has woken. USB devices come back under new nodes, so look for them now rather than at the next rescan.</summary>
    public void MachineWoke()
    {
        _input.Rescan();
    }

    private async Task JudgeAsync(CancellationToken ct)
    {
        var (locked, inhibitors) = await ReadLogindAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            var last = _input.Last;
            var verdict = PresenceRule.Judge(last?.At, last?.Device, _config.InUseIdleTime, _time.GetUtcNow(), locked, inhibitors);
            _report.Record(_input.Reading.ToList(), verdict);
            await SetInUseAsync(verdict);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SetInUseAsync(PresenceVerdict verdict)
    {
        if (verdict.InUse == _inUse)
        {
            return;
        }

        _inUse = verdict.InUse;
        _logger.LogInformation(_inUse ? "Someone is using this machine ({Reason}): taking no new jobs unless the server says to" : "Nobody is using this machine ({Reason}): taking jobs again", verdict.Reason);
        await _worker.SetHoldAsync(WorkerHold.InUse, _inUse);
    }

    /// <summary>Whether the screen is locked, and the inhibitors: without the host's bus, never locked and none, so presence rests on input alone.</summary>
    private async Task<(bool Locked, IReadOnlyList<LogindInhibitor> Inhibitors)> ReadLogindAsync(CancellationToken ct)
    {
        try
        {
            var sessions = await _logind.ListSessionsAsync(ct);
            var inhibitors = await _logind.ListInhibitorsAsync(ct);
            _logindReported = false;
            return (PresenceRule.IsLocked(sessions), inhibitors);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (!_logindReported)
            {
                _logindReported = true;
                _logger.LogWarning(ex, "Can't reach logind, so the screen lock and other programs keeping the machine awake are ignored; only input counts (mount the host's D-Bus socket, see docker-compose.inhibit-sleep.yml)");
            }
            else
            {
                _logger.LogDebug(ex, "Still can't reach logind");
            }
            return (false, []);
        }
    }
}
