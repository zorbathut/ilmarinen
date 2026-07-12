using Ilmarinen.Docker;
using NUnit.Framework;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Net;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Core.Tests;

/// <summary>
/// The diagnostic stands one of these up and tears it down on every worker startup, and a teardown that throws
/// takes the worker out of the fleet with it. These pin the disposal contract.
/// </summary>
[TestFixture]
public class AgentApiServerTests
{
    [Test]
    public async Task DisposeAsync_IsIdempotent()
    {
        var server = new AgentApiServer();

        await server.DisposeAsync();

        Assert.DoesNotThrowAsync(async () => await server.DisposeAsync());
    }

    // Disposal has to actually release the socket. It used to call both Stop() and Close(), and the second of
    // those re-binds the port to unregister its prefix — which throws if anything claimed the port in between.
    [Test]
    public async Task DisposeAsync_ReleasesThePort_WithoutThrowing()
    {
        var server = new AgentApiServer();
        var port = server.Port;

        Assert.DoesNotThrowAsync(async () => await server.DisposeAsync());

        var rebind = new TcpListener(IPAddress.Loopback, port);
        Assert.DoesNotThrow(() => rebind.Start());
        rebind.Stop();
    }

    [Test]
    public async Task Construction_AllocatesAServerThatAnswersOnItsOwnPort()
    {
        await using var server = new AgentApiServer();

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {server.Token}");
        var body = await client.GetStringAsync($"http://localhost:{server.Port}/api/ping");

        Assert.That(body, Does.Contain("ok"));
    }

    [Test]
    public async Task ConcurrentServers_GetDistinctPorts_AndAllTearDownCleanly()
    {
        var servers = new AgentApiServer[16];

        await Task.WhenAll(Enumerable.Range(0, servers.Length).Select(i => Task.Run(() =>
        {
            servers[i] = new AgentApiServer();
        })));

        Assert.That(servers.Select(s => s.Port).Distinct().Count(), Is.EqualTo(servers.Length));

        // Teardown races construction in the real suite: many workers and pipelines share one process, and a
        // freed ephemeral port can be handed straight back out to a sibling.
        Assert.DoesNotThrowAsync(async () =>
            await Task.WhenAll(servers.Select(s => s.DisposeAsync().AsTask())));
    }
}
