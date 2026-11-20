using Ilmarinen.Server.Services.Wake;
using Ilmarinen.Server;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>The wake-on-LAN packet, the MAC addresses an operator may type, and the sender putting the packet on the wire.</summary>
[TestFixture]
public class MagicPacketTests
{
    [TestCase("AA:BB:CC:DD:EE:0F", "aa:bb:cc:dd:ee:0f")]
    [TestCase("aa-bb-cc-dd-ee-0f", "aa:bb:cc:dd:ee:0f")]
    [TestCase("aabbccddee0f", "aa:bb:cc:dd:ee:0f")]
    [TestCase("  aa:bb:cc:dd:ee:0f ", "aa:bb:cc:dd:ee:0f")]
    [TestCase("aa:bb:cc:dd:ee", null)]
    [TestCase("aa:bb:cc:dd:ee:0g", null)]
    [TestCase("aa:bbcc:dd:ee:0f", null)]
    [TestCase("", null)]
    public void Normalize_AcceptsTheUsualForms(string text, string? normalized)
    {
        Assert.That(MagicPacket.Normalize(text), Is.EqualTo(normalized));
    }

    [Test]
    public void Build_IsSixFFsThenTheMacSixteenTimes()
    {
        var packet = MagicPacket.Build("01:23:45:67:89:ab");

        var mac = new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xab };
        var expected = Enumerable.Repeat((byte)0xFF, 6).Concat(Enumerable.Repeat(mac, 16).SelectMany(b => b)).ToArray();
        Assert.That(packet, Is.EqualTo(expected));
        Assert.That(packet, Has.Length.EqualTo(102));
    }

    [TestCase("192.168.1.255:7", "192.168.1.255:7")]
    [TestCase("192.168.1.255", "192.168.1.255:9")]
    public void ParseTarget_DefaultsToTheWakeOnLanPort(string address, string expected)
    {
        Assert.That(UdpWakeSender.ParseTarget(address).ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void ParseTarget_RejectsAHostName()
    {
        Assert.Throws<ConfigurationException>(() => UdpWakeSender.ParseTarget("lan-broadcast"));
    }

    [Test]
    public void WithNoAddress_WakingIsOff()
    {
        var sender = new UdpWakeSender(new ServerConfig(), NullLogger<UdpWakeSender>.Instance);

        Assert.That(sender.Enabled, Is.False);
        Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync("01:23:45:67:89:ab", CancellationToken.None));
    }

    [Test]
    public async Task Send_PutsThePacketOnTheWire()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        var sender = new UdpWakeSender(new ServerConfig { WakeAddress = $"127.0.0.1:{port}" }, NullLogger<UdpWakeSender>.Instance);

        Assert.That(sender.Enabled, Is.True);
        await sender.SendAsync("01:23:45:67:89:ab", CancellationToken.None);

        var received = await listener.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(received.Buffer, Is.EqualTo(MagicPacket.Build("01:23:45:67:89:ab")));
    }
}
