using Ilmarinen.Worker.Services;
using System.Threading;
using System;

namespace Ilmarinen.IntegrationTests.Fixtures;

/// <summary>A disk a test says how full it is, so no test depends on how full the machine running the suite is. Roomy unless a test says otherwise.</summary>
public sealed class FakeDiskSpace : IDiskSpace
{
    public const long Gb = 1024L * 1024 * 1024;

    private long _free = 500 * Gb;

    public long TotalBytes { get; set; } = 1000 * Gb;

    public long FreeBytes
    {
        get { return Interlocked.Read(ref _free); }
        set { Interlocked.Exchange(ref _free, value); }
    }

    /// <summary>When set, free space is whatever this says at each reading: for a test whose disk frees up as the worker deletes things.</summary>
    public Func<long>? Follow { get; set; }

    public DiskReading? Read(string path)
    {
        return new DiskReading(Follow?.Invoke() ?? FreeBytes, TotalBytes);
    }
}
