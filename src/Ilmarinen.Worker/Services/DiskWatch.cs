using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.Worker.Services;

/// <summary>
/// Watches free disk space while a job runs: records how low it gets, and stops the job (<see cref="Stopping"/>) before it takes free space below the worker's floor, so a runaway build fails instead of filling the disk of the machine it runs on.
/// </summary>
public sealed class DiskWatch : IAsyncDisposable
{
    // Short: at NVMe write speeds a few seconds is gigabytes, and a read is a statvfs call.
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly IDiskSpace _disk;
    private readonly string _path;
    private readonly long _minFreeBytes;
    private readonly ILogger _jobLogger;
    private readonly CancellationTokenSource _stop = new();
    private readonly PeriodicTimer _timer = new(Interval);
    private readonly object _lock = new();
    private long? _baseline;
    private long? _lowest;
    private bool _tripped;
    private Task? _loop;

    /// <param name="jobLogger">Where the reason for a stop is written: the job's own log, which is where whoever reads the failure will look.</param>
    public DiskWatch(IDiskSpace disk, string path, long minFreeBytes, ILogger jobLogger)
    {
        _disk = disk;
        _path = path;
        _minFreeBytes = minFreeBytes;
        _jobLogger = jobLogger;
    }

    /// <summary>Cancelled once free space has fallen below the floor.</summary>
    public CancellationToken Stopping
    {
        get { return _stop.Token; }
    }

    public bool StoppedForDisk
    {
        get
        {
            lock (_lock)
            {
                return _tripped;
            }
        }
    }

    /// <summary>How far free space fell below the baseline at its lowest; null if it was never read.</summary>
    public long? UsedBytes
    {
        get
        {
            lock (_lock)
            {
                return _baseline is { } baseline && _lowest is { } lowest ? Math.Max(0, baseline - lowest) : null;
            }
        }
    }

    /// <summary>Takes the baseline, free space as it stands now, and starts watching.</summary>
    public void Start()
    {
        var reading = _disk.Read(_path);
        lock (_lock)
        {
            _baseline = reading?.FreeBytes;
            _lowest = reading?.FreeBytes;
        }
        Observe(reading);
        _loop = Task.Run(async () =>
        {
            // Ends when disposed, which stops the timer.
            while (await _timer.WaitForNextTickAsync())
            {
                Observe(_disk.Read(_path));
            }
        });
    }

    /// <summary>One reading. Returns true if it stopped the job.</summary>
    public bool Observe(DiskReading? reading)
    {
        if (reading == null)
        {
            return false;
        }

        lock (_lock)
        {
            if (_baseline == null)
            {
                _baseline = reading.FreeBytes;
            }
            if (_lowest == null || reading.FreeBytes < _lowest)
            {
                _lowest = reading.FreeBytes;
            }
            if (reading.FreeBytes >= _minFreeBytes || _tripped)
            {
                return false;
            }
            _tripped = true;
        }

        _jobLogger.LogError("Free disk space fell to {Free:N0} bytes, below the {Floor:N0} this worker keeps free: stopping the job before it fills the disk", reading.FreeBytes, _minFreeBytes);
        _stop.Cancel();
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _timer.Dispose();
        if (_loop != null)
        {
            await _loop;
        }
        _stop.Dispose();
    }
}
