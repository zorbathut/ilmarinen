using Ilmarinen.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.Server.Services.Wake;

public enum WakeRequestResult
{
    Sent,
    NoSuchWorker,
    NoMacAddress,
    WakingOff
}

/// <summary>
/// Wakes sleeping workers' hosts with wake-on-LAN when queued jobs need them (<see cref="WakePlan"/>), and when an operator asks. Its state is in memory: a server restart forgets wakes in progress and backoffs, which costs at most a wake more than needed. It has a lock of its own and never takes the scheduler's: a wake decides nothing about the queue, and dispatch sends the woken worker its job once it is ready.
/// </summary>
public sealed class WorkerWaker
{
    // Long enough that a job just submitted while a worker is about to free up doesn't wake a desktop. It counts from submission, so a job handed back long after doesn't wait it out again.
    private static readonly TimeSpan QueuedDelay = TimeSpan.FromSeconds(30);

    // Longer than SignalR's 30s client timeout, by which the server has noticed a connection that died; a worker reconnecting, or every worker after a server restart, is not asleep.
    private static readonly TimeSpan DisconnectedDelay = TimeSpan.FromSeconds(90);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WorkerRepository _workers;
    private readonly IWakeSender _sender;
    private readonly WakeStatus _status;
    private readonly UIEventService _uiEvents;
    private readonly TimeProvider _time;
    private readonly WakeSettings _settings;
    private readonly ILogger<WorkerWaker> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public WorkerWaker(IServiceScopeFactory scopeFactory, WorkerRepository workers, IWakeSender sender, WakeStatus status, UIEventService uiEvents, TimeProvider time, ServerConfig config, ILogger<WorkerWaker> logger)
    {
        _scopeFactory = scopeFactory;
        _workers = workers;
        _sender = sender;
        _status = status;
        _uiEvents = uiEvents;
        _time = time;
        _settings = new WakeSettings(config.WakeTimeout, config.WakeBackoff, QueuedDelay, DisconnectedDelay);
        _logger = logger;
    }

    public bool Enabled
    {
        get { return _sender.Enabled; }
    }

    /// <summary>One round: end the wakes that came to something, wake sleepers for the jobs that need them, and send every wake in progress another packet.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        if (!_sender.Enabled)
        {
            return;
        }

        IReadOnlyList<WakeWorker> workers;
        WakeRound round;
        WakeState before;
        IReadOnlyDictionary<Guid, WakingWorker> waitingBefore;
        await _lock.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
            var queuedRows = await jobs.GetQueuedJobsAsync();
            var history = await jobs.GetDiskHistoryAsync(queuedRows.Select(j => (j.RepoUrl, j.ScriptPath)));
            var fleet = _workers.GetFleetDisks();
            workers = await _workers.GetWakeWorkersAsync();
            var disks = workers.Select(w => _workers.DiskOf(w.Id)).ToList();
            var queued = queuedRows
                .Select(j =>
                {
                    var runs = history.GetValueOrDefault((j.RepoUrl, j.ScriptPath)) ?? [];
                    var workspace = DiskEstimate.PredictedWorkspace(runs);
                    var need = DiskEstimate.NeedOnFleet(runs, fleet);
                    var noRoom = disks.Where(d => DiskEstimate.Fit(workspace, need, d) == null).Select(d => d.WorkerId).ToHashSet();
                    return new WakeJob(j.Id, j.MinWorkerRank, new DateTimeOffset(DateTime.SpecifyKind(j.CreatedAt, DateTimeKind.Utc)), noRoom);
                })
                .ToList();
            before = _status.State;
            waitingBefore = _status.WaitingForAll;
            round = WakePlan.Plan(queued, workers, before, _settings, _time.GetUtcNow());
            var named = workers.ToDictionary(w => w.Id, w => w.Name);
            _status.Set(round.State, round.WaitingFor.ToDictionary(w => w.Key, w => new WakingWorker(w.Value, named[w.Value])));
        }
        finally
        {
            _lock.Release();
        }

        var names = workers.ToDictionary(w => w.Id);
        foreach (var (workerId, end) in round.Ended)
        {
            var name = names[workerId].Name;
            switch (end)
            {
                case WakeEnd.Arrived:
                    _logger.LogInformation("Worker {Worker} woke and is back", name);
                    break;
                case WakeEnd.AwakeButNotReady:
                    _logger.LogInformation("Worker {Worker} woke but isn't taking jobs; leaving it be", name);
                    break;
                case WakeEnd.Unreachable:
                    _logger.LogWarning("Worker {Worker} didn't wake within {Timeout}; not trying it again for {Backoff}", name, _settings.Timeout, _settings.Backoff);
                    break;
            }
        }
        var started = round.State.Attempts.Keys.Where(id => !before.Attempts.ContainsKey(id)).ToList();
        foreach (var workerId in started)
        {
            _logger.LogInformation("Waking worker {Worker}", names[workerId].Name);
        }

        await SendAsync(round.Send.Select(id => names[id]), ct);
        if (round.Ended.Count > 0 || started.Count > 0 || !round.WaitingFor.OrderBy(w => w.Key).SequenceEqual(waitingBefore.OrderBy(w => w.Key).Select(w => KeyValuePair.Create(w.Key, w.Value.Id))))
        {
            _uiEvents.NotifyWorkersChanged();
            _uiEvents.NotifyJobsChanged();
        }
    }

    /// <summary>An operator's "Wake": send the packet now, as a wake in progress like any other, forgetting that the last one failed. A failure to send is thrown, for the page to show.</summary>
    public async Task<WakeRequestResult> WakeNowAsync(Guid workerId, CancellationToken ct)
    {
        if (!_sender.Enabled)
        {
            return WakeRequestResult.WakingOff;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        var worker = await db.Workers.Where(w => w.Id == workerId).Select(w => new { w.WakeMacAddress }).FirstOrDefaultAsync(ct);
        if (worker == null)
        {
            return WakeRequestResult.NoSuchWorker;
        }
        if (worker.WakeMacAddress is not { } mac)
        {
            return WakeRequestResult.NoMacAddress;
        }

        await _lock.WaitAsync(ct);
        try
        {
            var now = _time.GetUtcNow();
            var state = _status.State;
            _status.Set(state with
            {
                Attempts = state.Attempts.SetItem(workerId, new WakeAttempt(workerId, now, now + _settings.Timeout)),
                FailedAt = state.FailedAt.Remove(workerId)
            }, _status.WaitingForAll);
        }
        finally
        {
            _lock.Release();
        }
        _uiEvents.NotifyWorkersChanged();

        await _sender.SendAsync(mac, ct);
        return WakeRequestResult.Sent;
    }

    /// <summary>For the stuck report: the sleepers that can still be counted on to wake, and those whose last wake failed. Empty while waking is off.</summary>
    public async Task<(IReadOnlyList<WakeWorker> Wakeable, IReadOnlyList<WakeWorker> Failed)> GetSleepersAsync()
    {
        if (!_sender.Enabled)
        {
            return ([], []);
        }

        var state = _status.State;
        var sleepers = (await _workers.GetWakeWorkersAsync()).Where(w => !w.Connected).ToList();
        return (
            sleepers.Where(w => WakePlan.IsWakeable(w, state)).ToList(),
            sleepers.Where(w => state.FailedAt.ContainsKey(w.Id) && !w.Paused && w.Mac != null).OrderBy(w => w.Name).ToList());
    }

    private async Task SendAsync(IEnumerable<WakeWorker> workers, CancellationToken ct)
    {
        foreach (var worker in workers)
        {
            try
            {
                await _sender.SendAsync(worker.Mac!, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // The wake stays in progress: the next round sends again, and the timeout ends it if nothing ever gets through.
                _logger.LogWarning(ex, "Sending a wake-on-LAN packet to worker {Worker} failed", worker.Name);
            }
        }
    }
}
