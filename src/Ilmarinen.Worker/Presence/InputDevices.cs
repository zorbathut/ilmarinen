using System.Collections.Generic;
using System.Linq;
using System;

namespace Ilmarinen.Worker.Presence;

/// <summary>An input device a person drives: its event node (event3) and its name, for saying which device showed someone is there.</summary>
public sealed record InputDevice(string EventNode, string Name);

/// <summary>
/// The keyboards, mice, touchpads and touchscreens in /proc/bus/input/devices, which describes the whole host even inside a container. A device counts if the kernel gives it a keyboard (kbd) or mouse handler; that leaves out sound-card jack sensors, lid switches and accelerometers, which make events with nobody there. Devices on the host bus are left out too: the ACPI power and sleep buttons, the video bus's brightness keys and vendor hotkeys get a kbd handler, and some firmware reports a wake through them, which would make every wake look like someone arriving.
/// </summary>
public static class InputDevices
{
    public const string ListPath = "/proc/bus/input/devices";

    // BUS_HOST, in the kernel's input.h.
    private const string HostBus = "0019";

    public static IReadOnlyList<InputDevice> Parse(string text)
    {
        var devices = new List<InputDevice>();
        foreach (var block in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var name = "";
            var hostBus = false;
            string[] handlers = [];
            foreach (var line in block.Split('\n'))
            {
                if (line.StartsWith("I: Bus=", StringComparison.Ordinal))
                {
                    hostBus = line["I: Bus=".Length..].StartsWith(HostBus, StringComparison.OrdinalIgnoreCase);
                }
                else if (line.StartsWith("N: Name=", StringComparison.Ordinal))
                {
                    name = line["N: Name=".Length..].Trim().Trim('"');
                }
                else if (line.StartsWith("H: Handlers=", StringComparison.Ordinal))
                {
                    handlers = line["H: Handlers=".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                }
            }

            var node = handlers.FirstOrDefault(h => h.StartsWith("event", StringComparison.Ordinal));
            var driven = handlers.Any(h => h == "kbd" || IsMouseHandler(h));
            if (node != null && driven && !hostBus)
            {
                devices.Add(new InputDevice(node, name));
            }
        }
        return devices;
    }

    private static bool IsMouseHandler(string handler)
    {
        return handler.StartsWith("mouse", StringComparison.Ordinal) && handler.Length > "mouse".Length && handler["mouse".Length..].All(char.IsAsciiDigit);
    }
}
