using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System;

namespace Ilmarinen.Worker.Presence;

/// <summary>
/// When a person last touched a keyboard, mouse, touchpad or touchscreen, from the host's input event devices. Each device is read on its own thread, since a read blocks until there is input; only that something happened, and on which device, is kept, never what was typed.
/// </summary>
public sealed class InputActivity
{
    private readonly string _deviceDirectory;
    private readonly string _deviceList;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, InputDevice> _reading = new();
    private readonly object _rescanLock = new();
    private readonly object _lastLock = new();
    private (DateTimeOffset At, string Device)? _last;

    public InputActivity(string deviceDirectory, string deviceList, TimeProvider time, ILogger logger)
    {
        _deviceDirectory = deviceDirectory;
        _deviceList = deviceList;
        _time = time;
        _logger = logger;
        // A worker restarting (for an update, say) can't know whether someone is at the machine, reading with their hands off the keyboard; counting from now, it waits out the idle time before taking jobs, unless the screen is locked.
        _last = (time.GetUtcNow(), "none yet: counting from the worker's start");
    }

    /// <summary>The last input, and the device it came from; the start, until there is some; null after <see cref="Forget"/> until there is more.</summary>
    public (DateTimeOffset At, string Device)? Last
    {
        get
        {
            lock (_lastLock)
            {
                return _last;
            }
        }
    }

    /// <summary>The devices being read now.</summary>
    public IReadOnlyCollection<InputDevice> Reading
    {
        get { return _reading.Values.ToList(); }
    }

    /// <summary>Starts reading every keyboard or mouse not already being read: at the start, and again later for devices plugged in since, or that came back under a new node after the host slept.</summary>
    public void Rescan()
    {
        lock (_rescanLock)
        {
            RescanLocked();
        }
    }

    private void RescanLocked()
    {
        IReadOnlyList<InputDevice> devices;
        try
        {
            devices = InputDevices.Parse(File.ReadAllText(_deviceList));
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not list input devices from {Path}", _deviceList);
            return;
        }

        foreach (var device in devices)
        {
            var path = Path.Combine(_deviceDirectory, device.EventNode);
            if (_reading.ContainsKey(device.EventNode) || !File.Exists(path))
            {
                continue;
            }

            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Can't read input device {Device} ({Name})", path, device.Name);
                continue;
            }

            _reading[device.EventNode] = device;
            new Thread(() => Read(device, stream)) { IsBackground = true, Name = $"input {device.EventNode}" }.Start();
        }
    }

    /// <summary>Forgets the last input, so the machine counts as unattended until there is more: after it has slept, the input from before says nothing about who is there now.</summary>
    public void Forget()
    {
        lock (_lastLock)
        {
            _last = null;
        }
    }

    private void Read(InputDevice device, FileStream stream)
    {
        var buffer = new byte[InputEvent.Size * 64];
        try
        {
            using (stream)
            {
                var filled = 0;
                while (true)
                {
                    var read = stream.Read(buffer, filled, buffer.Length - filled);
                    if (read == 0)
                    {
                        _logger.LogInformation("Input device {Device} ({Name}) has gone", device.EventNode, device.Name);
                        return;
                    }
                    filled += read;

                    var whole = filled - filled % InputEvent.Size;
                    for (var offset = 0; offset < whole; offset += InputEvent.Size)
                    {
                        if (InputEvent.IsHumanInput(buffer.AsSpan(offset, InputEvent.Size)))
                        {
                            Record(device);
                            break;
                        }
                    }
                    // A partial event waits for the rest. The kernel only ever hands over whole events; a pipe needn't.
                    Array.Copy(buffer, whole, buffer, 0, filled - whole);
                    filled -= whole;
                }
            }
        }
        catch (IOException ex)
        {
            // A USB device unplugged, or re-enumerated after the host slept; the next rescan picks up whatever replaced it.
            _logger.LogWarning(ex, "Stopped reading input device {Device} ({Name})", device.EventNode, device.Name);
        }
        finally
        {
            _reading.TryRemove(device.EventNode, out _);
        }
    }

    private void Record(InputDevice device)
    {
        lock (_lastLock)
        {
            _last = (_time.GetUtcNow(), device.Name);
        }
    }
}
