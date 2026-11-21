using Microsoft.Extensions.Logging;
using System.IO;
using System;

namespace Ilmarinen.Worker.Services;

public sealed record DiskReading(long FreeBytes, long TotalBytes);

/// <summary>Free space on the filesystem holding a path. Abstracted so tests can say how full the disk is instead of depending on the machine they run on.</summary>
public interface IDiskSpace
{
    /// <summary>Null when it can't be read, having logged why.</summary>
    DiskReading? Read(string path);
}

/// <summary>
/// Reads the filesystem through statvfs. Inside a container that is the filesystem the workspace volume lives on, which for a named volume is also the Docker daemon's: images and build cache count against it too.
/// </summary>
public sealed class DriveDiskSpace : IDiskSpace
{
    private readonly ILogger<DriveDiskSpace> _logger;
    private bool _failureReported;

    public DriveDiskSpace(ILogger<DriveDiskSpace> logger)
    {
        _logger = logger;
    }

    public DiskReading? Read(string path)
    {
        try
        {
            var drive = new DriveInfo(path);
            var reading = new DiskReading(drive.AvailableFreeSpace, drive.TotalSize);
            _failureReported = false;
            return reading;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Read every few seconds: a failure that persists warns once.
            if (!_failureReported)
            {
                _failureReported = true;
                _logger.LogWarning(ex, "Can't read the free disk space at {Path}", path);
            }
            else
            {
                _logger.LogDebug(ex, "Still can't read the free disk space at {Path}", path);
            }
            return null;
        }
    }
}
