using Ilmarinen.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using System.Threading.Tasks;
using System.Threading;

namespace Ilmarinen.Core.Tests;

/// <summary>How low free space got during a job, and stopping the job before it goes below the floor.</summary>
[TestFixture]
public class DiskWatchTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private sealed class Disk : IDiskSpace
    {
        public long? Free { get; set; } = 100 * Gb;

        public DiskReading? Read(string path)
        {
            return Free is { } free ? new DiskReading(free, 1000 * Gb) : null;
        }
    }

    private static DiskWatch Watch(Disk disk, long floor = 5 * Gb)
    {
        var watch = new DiskWatch(disk, "/workspaces", floor, NullLogger.Instance);
        watch.Start();
        return watch;
    }

    [Test]
    public async Task Used_IsTheDeepestFallBelowTheBaseline()
    {
        var disk = new Disk();
        await using var watch = Watch(disk);

        watch.Observe(new DiskReading(70 * Gb, 1000 * Gb));
        watch.Observe(new DiskReading(90 * Gb, 1000 * Gb));

        Assert.That(watch.UsedBytes, Is.EqualTo(30 * Gb));
        Assert.That(watch.StoppedForDisk, Is.False);
        Assert.That(watch.Stopping.IsCancellationRequested, Is.False);
    }

    [Test]
    public async Task Used_IsNeverNegative_WhenSpaceIsFreedMeanwhile()
    {
        var disk = new Disk();
        await using var watch = Watch(disk);

        watch.Observe(new DiskReading(150 * Gb, 1000 * Gb));

        Assert.That(watch.UsedBytes, Is.Zero);
    }

    [Test]
    public async Task Start_CountsFromFreeSpaceThen()
    {
        // The worker freed 50 GB for the job before starting the watch: none of that is the job's.
        var disk = new Disk { Free = 130 * Gb };
        await using var watch = Watch(disk);
        watch.Observe(new DiskReading(110 * Gb, 1000 * Gb));

        Assert.That(watch.UsedBytes, Is.EqualTo(20 * Gb));
    }

    [Test]
    public async Task NotStarted_MeasuresNothing_AndNeverStops()
    {
        await using var watch = new DiskWatch(new Disk { Free = Gb }, "/workspaces", 5 * Gb, NullLogger.Instance);

        Assert.That(watch.UsedBytes, Is.Null);
        Assert.That(watch.StoppedForDisk, Is.False, "making room comes first");
    }

    [Test]
    public async Task BelowTheFloor_StopsTheJob_Once()
    {
        var disk = new Disk();
        await using var watch = Watch(disk);

        Assert.That(watch.Observe(new DiskReading(5 * Gb, 1000 * Gb)), Is.False, "at the floor is still allowed");
        Assert.That(watch.Observe(new DiskReading(4 * Gb, 1000 * Gb)), Is.True);
        Assert.That(watch.Observe(new DiskReading(3 * Gb, 1000 * Gb)), Is.False, "already stopped");

        Assert.That(watch.StoppedForDisk, Is.True);
        Assert.That(watch.Stopping.IsCancellationRequested, Is.True);
        Assert.That(watch.UsedBytes, Is.EqualTo(97 * Gb));
    }

    [Test]
    public async Task Unreadable_MeasuresNothing_AndNeverStops()
    {
        var disk = new Disk { Free = null };
        await using var watch = Watch(disk);

        watch.Observe(null);

        Assert.That(watch.UsedBytes, Is.Null);
        Assert.That(watch.StoppedForDisk, Is.False);
    }

    [Test]
    public async Task Started_ReadsOnItsOwn()
    {
        var disk = new Disk();
        await using var watch = new DiskWatch(disk, "/workspaces", 5 * Gb, NullLogger.Instance);
        watch.Start();

        disk.Free = 2 * Gb;
        await Task.WhenAny(Task.Delay(Timeout.Infinite, watch.Stopping), Task.Delay(10_000));

        Assert.That(watch.StoppedForDisk, Is.True);
    }
}
