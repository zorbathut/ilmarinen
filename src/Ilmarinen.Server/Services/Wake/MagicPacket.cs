using System.Globalization;
using System.Linq;
using System;

namespace Ilmarinen.Server.Services.Wake;

/// <summary>Wake-on-LAN's magic packet, and the MAC addresses it is built from.</summary>
public static class MagicPacket
{
    public const int Size = 6 + 16 * 6;

    /// <summary>A MAC address as aa:bb:cc:dd:ee:ff, from that form, its dashed form, or twelve bare hex digits; null if it is none of these.</summary>
    public static string? Normalize(string text)
    {
        var hex = new string(text.Trim().Where(c => c is not (':' or '-')).ToArray());
        var separated = text.Trim().Length is 17 or 12;
        if (!separated || hex.Length != 12 || !hex.All(char.IsAsciiHexDigit))
        {
            return null;
        }
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2).ToLowerInvariant()));
    }

    /// <summary>Six 0xFF bytes, then the MAC sixteen times.</summary>
    public static byte[] Build(string mac)
    {
        var normalized = Normalize(mac) ?? throw new ArgumentException($"'{mac}' is not a MAC address", nameof(mac));
        var address = normalized.Split(':').Select(b => byte.Parse(b, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
        var packet = new byte[Size];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (var i = 0; i < 16; i++)
        {
            address.CopyTo(packet, 6 + i * 6);
        }
        return packet;
    }
}
