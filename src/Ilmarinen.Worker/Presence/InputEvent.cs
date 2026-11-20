using System.Buffers.Binary;
using System;

namespace Ilmarinen.Worker.Presence;

/// <summary>The kernel's struct input_event on a 64-bit host: a timeval (two 8-byte fields), then type, code and value.</summary>
public static class InputEvent
{
    public const int Size = 24;

    private const ushort EvKey = 0x01;
    private const ushort EvRel = 0x02;
    private const ushort EvAbs = 0x03;

    /// <summary>Whether the event is a key, button, movement or touch. Sync markers, scan codes, switches, LEDs and autorepeat settings are left out: they follow from real input, or come with nobody there.</summary>
    public static bool IsHumanInput(ReadOnlySpan<byte> inputEvent)
    {
        var type = BinaryPrimitives.ReadUInt16LittleEndian(inputEvent.Slice(16, 2));
        return type is EvKey or EvRel or EvAbs;
    }
}
