using Ilmarinen.Server.Hubs;
using NUnit.Framework;
using System.Net;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// The server binds with ListenAnyIP, which creates a dual-stack socket, so IPv4 workers arrive
/// with IPv4-mapped IPv6 peer addresses (::ffff:10.0.0.5). The integration fixture binds with
/// ListenLocalhost — separate v4 and v6 sockets — and so can never produce a mapped address,
/// which is why the normalization is pinned here rather than through a connected worker.
/// </summary>
[TestFixture]
[Category("Integration")]
public class WorkerAddressTests
{
    [Test]
    public void AddressFormat_IPv4Mapped_ReturnsDottedQuad()
    {
        Assert.That(WorkerHub.AddressFormat(IPAddress.Parse("::ffff:10.0.0.5")), Is.EqualTo("10.0.0.5"));
    }

    [Test]
    public void AddressFormat_IPv4_ReturnsUnchanged()
    {
        Assert.That(WorkerHub.AddressFormat(IPAddress.Parse("10.0.0.5")), Is.EqualTo("10.0.0.5"));
    }

    [Test]
    public void AddressFormat_IPv6_ReturnsUnchanged()
    {
        Assert.That(WorkerHub.AddressFormat(IPAddress.Parse("2001:db8::1")), Is.EqualTo("2001:db8::1"));
    }

    [Test]
    public void AddressFormat_IPv6Loopback_ReturnsUnchanged()
    {
        Assert.That(WorkerHub.AddressFormat(IPAddress.IPv6Loopback), Is.EqualTo("::1"));
    }

    [Test]
    public void AddressFormat_Null_ReturnsNull()
    {
        Assert.That(WorkerHub.AddressFormat(null), Is.Null);
    }
}
