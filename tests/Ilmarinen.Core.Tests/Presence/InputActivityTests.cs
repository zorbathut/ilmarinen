using Ilmarinen.Worker.Presence;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Core.Tests.Presence;

/// <summary>The device reader, fed through a named pipe standing in for /dev/input/event3: real input is noticed and stamped, other events aren't, and a device that goes away is let go.</summary>
[TestFixture]
public class InputActivityTests
{
    private string _dir = null!;

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string path, uint mode);

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-19T12:00:00Z");

        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }
    }

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ilmarinen-input-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_dir, recursive: true);
    }

    private static byte[] Event(ushort type)
    {
        var bytes = new byte[InputEvent.Size];
        BitConverter.TryWriteBytes(bytes.AsSpan(16, 2), type);
        return bytes;
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }
    }

    [Test]
    public async Task AKeyPress_IsStampedWithItsDevice_SyncAloneIsNot_AndAClosedDeviceIsLetGo()
    {
        var list = Path.Combine(_dir, "devices");
        File.WriteAllText(list, "N: Name=\"Test Keyboard\"\nH: Handlers=sysrq kbd leds event3\n");
        var node = Path.Combine(_dir, "event3");
        Assert.That(mkfifo(node, Convert.ToUInt32("600", 8)), Is.Zero);
        var time = new ManualTime();
        var activity = new InputActivity(_dir, list, time, NullLogger.Instance);
        activity.Forget();

        // Opening either end of a FIFO waits for the other.
        var opening = Task.Run(() => new FileStream(node, FileMode.Open, FileAccess.Write));
        activity.Rescan();
        await using var device = await opening;
        Assert.That(activity.Reading, Has.Count.EqualTo(1));

        await device.WriteAsync(Event(0x00));
        await device.FlushAsync();
        await Task.Delay(200);
        Assert.That(activity.Last, Is.Null, "a sync marker is no one typing");

        var key = Event(0x01);
        await device.WriteAsync(key.AsMemory(0, 10));
        await device.FlushAsync();
        await Task.Delay(100);
        Assert.That(activity.Last, Is.Null, "half an event is not yet an event");
        await device.WriteAsync(key.AsMemory(10));
        await device.FlushAsync();
        await Until(() => activity.Last != null);
        Assert.That(activity.Last, Is.EqualTo((time.Now, "Test Keyboard")));

        activity.Forget();
        Assert.That(activity.Last, Is.Null);

        await device.DisposeAsync();
        await Until(() => activity.Reading.Count == 0);
        Assert.That(activity.Reading, Is.Empty);
    }

    // A restarted worker can't tell a person reading, hands off the keyboard, from nobody there.
    [Test]
    public void AtTheStart_CountsAsInputNow()
    {
        var time = new ManualTime();

        var activity = new InputActivity(_dir, Path.Combine(_dir, "devices"), time, NullLogger.Instance);

        Assert.That(activity.Last?.At, Is.EqualTo(time.Now));
    }

    [Test]
    public void Rescan_SkipsDevicesThatArentThere()
    {
        var list = Path.Combine(_dir, "devices");
        File.WriteAllText(list, "N: Name=\"Gone\"\nH: Handlers=kbd event9\n");
        var activity = new InputActivity(_dir, list, new ManualTime(), NullLogger.Instance);

        activity.Rescan();

        Assert.That(activity.Reading, Is.Empty);
    }
}
