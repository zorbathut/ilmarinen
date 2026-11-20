using Microsoft.Extensions.Logging;
using System.Net.Sockets;
using System.Net;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.Server.Services.Wake;

/// <summary>Sends wake-on-LAN packets.</summary>
public interface IWakeSender
{
    /// <summary>Whether there is anywhere to send them: false until Server__WakeAddress is set.</summary>
    bool Enabled { get; }

    Task SendAsync(string mac, CancellationToken ct);
}

/// <summary>Sends each packet a few times over UDP to the configured broadcast address: the packet is unacknowledged and easily lost, and a machine woken twice is no worse off.</summary>
public sealed class UdpWakeSender : IWakeSender
{
    private const int DefaultPort = 9;
    private const int Repeats = 3;

    private readonly IPEndPoint? _target;
    private readonly ILogger<UdpWakeSender> _logger;

    public UdpWakeSender(ServerConfig config, ILogger<UdpWakeSender> logger)
    {
        _target = config.WakeAddress is { } address ? ParseTarget(address) : null;
        _logger = logger;
    }

    public bool Enabled
    {
        get { return _target != null; }
    }

    /// <summary>address:port, or a bare address on the usual wake-on-LAN port.</summary>
    public static IPEndPoint ParseTarget(string address)
    {
        if (IPEndPoint.TryParse(address, out var endPoint))
        {
            return endPoint.Port == 0 ? new IPEndPoint(endPoint.Address, DefaultPort) : endPoint;
        }
        throw new ConfigurationException($"Server__WakeAddress must be an IP address, optionally with a port (192.168.1.255:9); got '{address}'.");
    }

    public async Task SendAsync(string mac, CancellationToken ct)
    {
        var target = _target ?? throw new InvalidOperationException("Waking workers is off: Server__WakeAddress is not set");
        var packet = MagicPacket.Build(mac);
        using var udp = new UdpClient(target.AddressFamily) { EnableBroadcast = true };
        for (var i = 0; i < Repeats; i++)
        {
            await udp.SendAsync(packet, target, ct);
        }
        _logger.LogInformation("Sent a wake-on-LAN packet for {Mac} to {Target}", mac, target);
    }
}
