using Ilmarinen.Docker;
using Ilmarinen.Protocol.Metrics;
using Ilmarinen.Protocol.Requests;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.Worker.Metrics;

/// <summary>
/// Samples the host and the job's containers while a job runs, and streams the records to the server as JobMetricsChunks (MetricsRecords describes them). It runs beside the job and never holds it up: sends are never awaited where the pipeline waits, and a sample that fails is logged and the next one tried.
/// </summary>
public sealed class JobMetricsSampler
{
    /// <summary>
    /// The most a chunk may hold, in UTF-8 bytes of content. Kept well under SignalR's 32 KB message limit, with room for the JSON protocol escaping every quote in the content again: a message over the limit closes the connection, and replaying it would fail every handshake after.
    /// </summary>
    public const int ChunkBudgetBytes = 16 * 1024;

    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);

    private readonly Guid _jobId;
    private readonly Guid _assignmentId;
    private readonly TimeSpan _interval;
    private readonly IHostSource _host;
    private readonly IContainerSource _containers;
    private readonly string? _workerContainerId;
    private readonly Func<JobMetricsChunk, Task> _send;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    // One sample at a time, whether the timer or a step ending asked for it; it also guards the batch and the step being gathered. Never held across a send.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StringBuilder _batch = new();

    // Chunks taken from the batch and not yet handed to the sender; drained under _gate, sent after it is released.
    private readonly List<JobMetricsChunk> _ready = new();
    private readonly Dictionary<string, ContainerCounters> _previous = new();
    private readonly Dictionary<string, ContainerDetails?> _known = new();
    private readonly Dictionary<string, string> _roles = new();
    private readonly Dictionary<string, ContainerFigures> _latest = new();
    private readonly CancellationTokenSource _stop = new();

    private string _hostLine = "";
    private int _batchBytes;
    private HostSnapshot? _previousHost;
    private DateTimeOffset _lastFlush;
    private DateTimeOffset? _lastSample;
    private int _sequence;
    private volatile Func<PipelineProgress?> _progress = () => null;
    private StepAccumulator? _step;
    private Task _loop = Task.CompletedTask;
    private bool _reportedMissingHostParts;
    private bool _reportedContainerFailure;
    private bool _reportedOversizedSample;

    /// <param name="send">Delivers a chunk, buffering it for replay if it can't, and never throws: the worker's BufferedHubSender. Not awaited where the pipeline waits.</param>
    public JobMetricsSampler(Guid jobId, Guid assignmentId, TimeSpan interval, IHostSource host, IContainerSource containers, string? workerContainerId, Func<JobMetricsChunk, Task> send, ILogger logger, TimeProvider time)
    {
        _jobId = jobId;
        _assignmentId = assignmentId;
        _interval = interval;
        _host = host;
        _containers = containers;
        _workerContainerId = workerContainerId;
        _send = send;
        _logger = logger;
        _time = time;
    }

    /// <summary>Starts sampling on a timer, beginning with the host record that heads every chunk.</summary>
    public void Start()
    {
        var now = _time.GetUtcNow();
        _hostLine = MetricsRecords.Serialize(_host.Describe(now)) + "\n";
        _lastFlush = now;
        _loop = RunAsync(_stop.Token);
    }

    /// <summary>Lets samples say which step is running and measure its containers, once the pipeline has a runner.</summary>
    public void AttachPipeline(Func<PipelineProgress?> progress)
    {
        _progress = progress;
    }

    /// <summary>
    /// Records a step or pull boundary; a step's end also gets its summary. A step's container ending gets one last look at the containers first, while its counters can still be read. The pipeline awaits this, so it never waits on a send, and a failure is logged here rather than failing the step.
    /// </summary>
    public async Task OnPipelineEventAsync(PipelineEvent e)
    {
        IReadOnlyList<JobMetricsChunk> ready;
        await _gate.WaitAsync();
        try
        {
            if (e.Kind == PipelineEventKind.StepContainerEnding)
            {
                await SampleAsync(includeHost: false);
            }
            else
            {
                Record(e);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record {Event} in job {JobId}'s metrics", e.Kind, _jobId);
        }
        finally
        {
            ready = DrainReady();
            _gate.Release();
        }
        Send(ready);
    }

    /// <summary>Takes one sample, and sends the batch if it is due.</summary>
    public async Task TickAsync()
    {
        IReadOnlyList<JobMetricsChunk> ready;
        await _gate.WaitAsync();
        try
        {
            await SampleAsync(includeHost: true);
            if (_time.GetUtcNow() - _lastFlush >= FlushInterval)
            {
                TakeBatch();
            }
        }
        finally
        {
            ready = DrainReady();
            _gate.Release();
        }
        Send(ready);
    }

    /// <summary>Stops the timer, takes a last sample and hands what is left to the sender, without waiting for it to arrive: it is buffered for replay if it doesn't. Never throws: it runs on the way to reporting the job's outcome.</summary>
    public async Task StopAsync()
    {
        await _stop.CancelAsync();
        await _loop;

        IReadOnlyList<JobMetricsChunk> ready;
        await _gate.WaitAsync();
        try
        {
            await SampleAsync(includeHost: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not take job {JobId}'s last metrics sample", _jobId);
        }
        finally
        {
            TakeBatch();
            ready = DrainReady();
            _gate.Release();
        }
        Send(ready);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_interval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await TickAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "A metrics sample for job {JobId} failed; trying again next interval", _jobId);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped.
        }
    }

    /// <summary>A pipeline event as its record, and the step it starts or finishes. Caller holds _gate.</summary>
    private void Record(PipelineEvent e)
    {
        switch (e.Kind)
        {
            case PipelineEventKind.StepStarted:
            {
                var start = new StepRecord { Ts = e.Timestamp, Index = e.StepIndex!.Value, Name = e.StepName!, Event = "start", Image = e.Image, ImageFromEarlierStep = e.ImageFromEarlierStep };
                _step = new StepAccumulator(start, new Dictionary<string, ContainerFigures>(_latest));
                Append(start);
                return;
            }
            case PipelineEventKind.StepEnded:
            {
                var end = new StepRecord { Ts = e.Timestamp, Index = e.StepIndex!.Value, Name = e.StepName!, Event = "end", Status = e.Status };
                Append(end);
                if (_step?.Index == end.Index)
                {
                    Append(_step.Finish(end));
                    _step = null;
                }
                return;
            }
            case PipelineEventKind.PullStarted:
            case PipelineEventKind.PullEnded:
            {
                var pull = new PullRecord { Ts = e.Timestamp, Image = e.Image!, Event = e.Kind == PipelineEventKind.PullStarted ? "start" : "end" };
                _step?.Add(pull);
                Append(pull);
                return;
            }
        }
    }

    /// <summary>
    /// One sample record. Host figures are left out of the extra sample at a step's end: it falls milliseconds after the last, and rates over so short an interval are mostly rounding. Caller holds _gate.
    /// </summary>
    private async Task SampleAsync(bool includeHost)
    {
        var now = _time.GetUtcNow();
        var progress = _progress();

        HostFigures? host = null;
        if (includeHost)
        {
            try
            {
                var snapshot = _host.Read(now);
                ReportMissingHostParts(snapshot);
                host = HostStats.Between(_previousHost, snapshot);
                _previousHost = snapshot;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read host figures for job {JobId}", _jobId);
            }
        }

        // Bounded, so a daemon slowed by the very build being measured can't stall sampling.
        using var timeout = new CancellationTokenSource(_interval > TimeSpan.FromSeconds(1) ? _interval : TimeSpan.FromSeconds(1));
        var (containers, announced) = await SampleContainersAsync(progress, now, timeout.Token);

        int? cotenants = null;
        try
        {
            cotenants = await _containers.CountCotenantsAsync(progress?.NetworkName, timeout.Token);
        }
        catch (Exception ex)
        {
            ReportContainerFailure(ex);
        }

        var sample = new SampleRecord
        {
            Ts = now,
            IntervalSeconds = _lastSample is { } last ? (now - last).TotalSeconds : 0,
            Phase = progress == null ? "prepare" : "pipeline",
            StepIndex = progress?.StepIndex,
            Step = progress?.StepName,
            Cotenants = cotenants,
            Host = host,
            Containers = containers
        };
        if (includeHost)
        {
            _lastSample = now;
        }
        _step?.Add(sample);

        foreach (var record in announced)
        {
            Append(record);
        }
        Append(FitToChunk(sample));
    }

    private async Task<(IReadOnlyList<ContainerFigures> Figures, IReadOnlyList<ContainerRecord> Announced)> SampleContainersAsync(PipelineProgress? progress, DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<ContainerSeen> seen;
        try
        {
            seen = await _containers.ListAsync(progress?.NetworkName, cancellationToken);
        }
        catch (Exception ex)
        {
            ReportContainerFailure(ex);
            return ([], []);
        }

        var announced = new List<ContainerRecord>();
        foreach (var container in seen.Where(c => !_known.ContainsKey(c.Id)))
        {
            ContainerDetails? details = null;
            try
            {
                details = await _containers.InspectAsync(container.Id, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not inspect container {ContainerId} for job {JobId}'s metrics", container.Id, _jobId);
            }
            _known[container.Id] = details;
            _roles[container.Id] = RoleOf(container.Id, progress);
            announced.Add(new ContainerRecord
            {
                Ts = now,
                Id = ShortId(container.Id),
                Name = container.Name?.TrimStart('/'),
                Image = details?.Image,
                Role = _roles[container.Id],
                StartedAt = details?.StartedAt
            });
        }

        var stats = await Task.WhenAll(seen.Select(async c =>
        {
            try
            {
                return (c.Id, Stats: await _containers.StatsAsync(c.Id, cancellationToken));
            }
            catch (Exception ex)
            {
                // Gone between listing and measuring, or too slow to answer: skipped this time.
                _logger.LogDebug(ex, "No stats for container {ContainerId} in job {JobId}'s sample", c.Id, _jobId);
                return (c.Id, Stats: null);
            }
        }));

        var figures = new List<ContainerFigures>();
        foreach (var (id, response) in stats)
        {
            if (response == null)
            {
                continue;
            }
            var counters = ContainerStats.FromStats(ShortId(id), response);
            _previous.TryGetValue(id, out var previous);
            var current = ContainerStats.Between(previous, counters, _known.GetValueOrDefault(id)?.StartedAt, _roles[id]);
            figures.Add(current);
            _previous[id] = counters;
            _latest[current.Id] = current;
        }
        return (figures, announced);
    }

    private string RoleOf(string id, PipelineProgress? progress)
    {
        if (_workerContainerId != null && (id.StartsWith(_workerContainerId, StringComparison.Ordinal) || _workerContainerId.StartsWith(id, StringComparison.Ordinal)))
        {
            return "worker";
        }
        return progress?.StepContainerId != null && id.StartsWith(progress.StepContainerId, StringComparison.Ordinal) ? "step" : "other";
    }

    private static string ShortId(string id)
    {
        return id.Length > 12 ? id[..12] : id;
    }

    /// <summary>A sample small enough to go in one chunk on its own, leaving out containers if it must; it would take a very crowded job network to need it.</summary>
    private SampleRecord FitToChunk(SampleRecord sample)
    {
        var fitted = sample;
        while (Bytes(MetricsRecords.Serialize(fitted)) > ChunkBudgetBytes - Bytes(_hostLine) && fitted.Containers.Count > 0)
        {
            var kept = fitted.Containers.Count / 2;
            fitted = sample with { Containers = sample.Containers.Take(kept).ToList(), ContainersOmitted = sample.Containers.Count - kept };
        }
        if (fitted != sample && !_reportedOversizedSample)
        {
            _reportedOversizedSample = true;
            _logger.LogWarning("Job {JobId} has {Count} containers on its network, too many for one metrics chunk; samples leave some out", _jobId, sample.Containers.Count);
        }
        return fitted;
    }

    /// <summary>Adds a record to the batch, first setting the batch aside to send if the record wouldn't fit in it. Caller holds _gate.</summary>
    private void Append(MetricsRecord record)
    {
        var line = MetricsRecords.Serialize(record) + "\n";
        var bytes = Bytes(line);
        if (_batchBytes > 0 && Bytes(_hostLine) + _batchBytes + bytes > ChunkBudgetBytes)
        {
            TakeBatch();
        }
        _batch.Append(line);
        _batchBytes += bytes;
    }

    /// <summary>Sets the batch aside as a chunk headed by the host record, and starts a fresh one. Caller holds _gate.</summary>
    private void TakeBatch()
    {
        _lastFlush = _time.GetUtcNow();
        if (_batch.Length == 0)
        {
            return;
        }

        _ready.Add(new JobMetricsChunk
        {
            JobId = _jobId,
            AssignmentId = _assignmentId,
            SequenceNumber = _sequence++,
            Content = _hostLine + _batch,
            Timestamp = _lastFlush.UtcDateTime
        });
        _batch.Clear();
        _batchBytes = 0;
    }

    /// <summary>Caller holds _gate.</summary>
    private IReadOnlyList<JobMetricsChunk> DrainReady()
    {
        var ready = _ready.ToList();
        _ready.Clear();
        return ready;
    }

    /// <summary>Hands chunks to the sender without waiting: it never throws, and buffers for replay whatever it can't deliver.</summary>
    private void Send(IReadOnlyList<JobMetricsChunk> chunks)
    {
        foreach (var chunk in chunks)
        {
            _ = _send(chunk);
        }
    }

    private static int Bytes(string text)
    {
        return Encoding.UTF8.GetByteCount(text);
    }

    private void ReportMissingHostParts(HostSnapshot snapshot)
    {
        if (_reportedMissingHostParts)
        {
            return;
        }
        var missing = new List<string>();
        if (snapshot.Cpu == null)
        {
            missing.Add("cpu (/proc/stat)");
        }
        if (snapshot.MemInfoKb == null)
        {
            missing.Add("memory (/proc/meminfo)");
        }
        if (snapshot.Disks == null)
        {
            missing.Add("disk (/proc/diskstats)");
        }
        if (snapshot.CpuPressure == null || snapshot.MemPressure == null || snapshot.IoPressure == null)
        {
            missing.Add("pressure (/proc/pressure)");
        }
        if (missing.Count > 0)
        {
            _reportedMissingHostParts = true;
            _logger.LogWarning("Job {JobId}'s metrics have no host {Parts}: this host doesn't offer them", _jobId, string.Join(", ", missing));
        }
    }

    /// <summary>The Docker daemon not answering is worth saying once per job; every sample after would only repeat it.</summary>
    private void ReportContainerFailure(Exception ex)
    {
        if (!_reportedContainerFailure)
        {
            _reportedContainerFailure = true;
            _logger.LogWarning(ex, "Could not list job {JobId}'s containers for metrics; samples go without them while it lasts", _jobId);
        }
        else
        {
            _logger.LogDebug(ex, "Could not list job {JobId}'s containers for metrics", _jobId);
        }
    }
}
