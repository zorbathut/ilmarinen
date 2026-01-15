using System.Net;
using Ilmarinen.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.SignalR.Client;
using NUnit.Framework;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
[Category("Integration")]
public class PortIsolationTests
{
    private IntegrationTestFixture _fixture = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new IntegrationTestFixture();
        await _fixture.SetupAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _fixture.DisposeAsync();
    }

    [Test]
    public async Task WorkerHub_OnPublicPort_ReturnsNotFound()
    {
        // Arrange - Try to connect to /workers on the public port
        var connection = new HubConnectionBuilder()
            .WithUrl($"{_fixture.ServerUrl}/workers")
            .Build();

        // Act & Assert - Connection should fail because /workers is blocked on public port
        var ex = Assert.ThrowsAsync<HttpRequestException>(
            async () => await connection.StartAsync());

        // The middleware returns 404 Not Found
        Assert.That(ex!.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        await connection.DisposeAsync();
    }

    [Test]
    public async Task ApiEndpoints_OnWorkerPort_ReturnsNotFound()
    {
        // Arrange - Create client pointing to worker port
        using var client = new HttpClient
        {
            BaseAddress = new Uri(_fixture.WorkerUrl)
        };

        // Act
        var response = await client.GetAsync("/api/jobs");

        // Assert - API endpoints blocked on worker port
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task WorkerHub_OnWorkerPort_Succeeds()
    {
        // Arrange - Connect to /workers on the correct worker port
        var connection = new HubConnectionBuilder()
            .WithUrl($"{_fixture.WorkerUrl}/workers")
            .Build();

        // Act
        await connection.StartAsync();

        // Assert - Connection succeeded
        Assert.That(connection.State, Is.EqualTo(HubConnectionState.Connected));

        await connection.StopAsync();
        await connection.DisposeAsync();
    }

    [Test]
    public async Task ApiEndpoints_OnPublicPort_Succeeds()
    {
        // Arrange - Create client pointing to public port
        using var client = new HttpClient
        {
            BaseAddress = new Uri(_fixture.ServerUrl)
        };

        // Act
        var response = await client.GetAsync("/api/jobs");

        // Assert - API endpoints work on public port
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Worker_ConnectsOnWorkerPort_CanRegister()
    {
        // Act - Start worker (which uses WorkerUrl for SignalR)
        var workerId = await _fixture.StartWorkerAsync();

        // Assert - Worker registration succeeded
        Assert.That(workerId, Is.Not.EqualTo(default(NUlid.Ulid)));
    }

    [Test]
    public async Task HealthCheck_OnPublicPort_Succeeds()
    {
        // Arrange
        using var client = new HttpClient
        {
            BaseAddress = new Uri(_fixture.ServerUrl)
        };

        // Act
        var response = await client.GetAsync("/health");

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task HealthCheck_OnWorkerPort_ReturnsNotFound()
    {
        // Arrange
        using var client = new HttpClient
        {
            BaseAddress = new Uri(_fixture.WorkerUrl)
        };

        // Act
        var response = await client.GetAsync("/health");

        // Assert - Health check blocked on worker port
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
