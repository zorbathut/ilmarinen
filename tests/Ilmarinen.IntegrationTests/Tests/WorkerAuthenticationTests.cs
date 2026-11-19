using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Ilmarinen.Worker;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Net.Http.Json;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
[Category("Integration")]
public class WorkerAuthenticationTests
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

    private HubConnection CreateHubConnection()
    {
        return new HubConnectionBuilder()
            .WithUrl($"{_fixture.WorkerUrl}/hub/workers")
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
            })
            .Build();
    }

    private async Task<WorkerRegistrationResult> RegisterWorkerAsync(string name)
    {
        using var scope = _fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WorkerRegistrationService>();
        return await service.RegisterWorkerAsync(name);
    }

    // --- SignalR Connection Failures ---

    [Test]
    public async Task Connect_UnknownWorker_Throws()
    {
        await using var connection = CreateHubConnection();
        await connection.StartAsync();

        var ex = Assert.ThrowsAsync<HubException>(async () =>
            await connection.InvokeAsync<AuthChallenge>("Connect", new WorkerConnect
            {
                WorkerId = Guid.CreateVersion7(),
                ProtocolHash = ProtocolVersion.Hash,
                Nonce = RandomNumberGenerator.GetBytes(32),
                InstanceId = Guid.NewGuid(),
                ConnectionCounter = 1
            }));

        Assert.That(ex!.Message, Does.Contain("Unknown worker"));
    }

    [Test]
    public async Task Connect_ProtocolMismatch_Throws()
    {
        var result = await RegisterWorkerAsync("build-mismatch-worker");

        await using var connection = CreateHubConnection();
        await connection.StartAsync();

        var ex = Assert.ThrowsAsync<HubException>(async () =>
            await connection.InvokeAsync<AuthChallenge>("Connect", new WorkerConnect
            {
                WorkerId = result.WorkerId,
                ProtocolHash = "wrong-hash",
                Nonce = RandomNumberGenerator.GetBytes(32),
                InstanceId = Guid.NewGuid(),
                ConnectionCounter = 1
            }));

        Assert.That(ex!.Message, Does.Contain("Protocol mismatch"));
    }

    [Test]
    public async Task Authenticate_WithoutConnect_Throws()
    {
        await using var connection = CreateHubConnection();
        await connection.StartAsync();

        var ex = Assert.ThrowsAsync<HubException>(async () =>
            await connection.InvokeAsync("Authenticate", new WorkerAuthenticate
            {
                Signature = RandomNumberGenerator.GetBytes(64)
            }));

        Assert.That(ex!.Message, Does.Contain("No pending authentication challenge"));
    }

    [Test]
    public async Task Authenticate_WrongSignature_Throws()
    {
        var result = await RegisterWorkerAsync("wrong-sig-worker");

        await using var connection = CreateHubConnection();
        await connection.StartAsync();

        var workerNonce = RandomNumberGenerator.GetBytes(32);
        var instanceId = Guid.NewGuid();
        var challenge = await connection.InvokeAsync<AuthChallenge>("Connect", new WorkerConnect
        {
            WorkerId = result.WorkerId,
            ProtocolHash = ProtocolVersion.Hash,
            Nonce = workerNonce,
            InstanceId = instanceId,
            ConnectionCounter = 1
        });

        // Sign with a completely different key
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var challengeData = HandshakeChallenge.Build(result.WorkerId, instanceId, 1, workerNonce, challenge.Nonce);
        var badSignature = wrongKey.SignData(challengeData, HashAlgorithmName.SHA256);

        var ex = Assert.ThrowsAsync<HubException>(async () =>
            await connection.InvokeAsync("Authenticate", new WorkerAuthenticate
            {
                Signature = badSignature
            }));

        Assert.That(ex!.Message, Does.Contain("Invalid signature"));
    }

    [Test]
    public async Task Connect_RevokedWorker_Throws()
    {
        var result = await RegisterWorkerAsync("revoked-worker");

        // Revoke the worker
        using (var scope = _fixture.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<WorkerRegistrationService>();
            await service.RevokeWorkerAsync(result.WorkerId);
        }

        await using var connection = CreateHubConnection();
        await connection.StartAsync();

        var ex = Assert.ThrowsAsync<HubException>(async () =>
            await connection.InvokeAsync<AuthChallenge>("Connect", new WorkerConnect
            {
                WorkerId = result.WorkerId,
                ProtocolHash = ProtocolVersion.Hash,
                Nonce = RandomNumberGenerator.GetBytes(32),
                InstanceId = Guid.NewGuid(),
                ConnectionCounter = 1
            }));

        Assert.That(ex!.Message, Does.Contain("Unknown worker"));
    }

    // --- Fencing over the wire ---

    // A superseded connection learns it on its next call, which is how a worker whose connection went quiet finds out it has to reconnect.
    [Test]
    public async Task Heartbeat_OnASupersededConnection_IsAnsweredFenced()
    {
        var result = await RegisterWorkerAsync("superseded-worker");
        var instanceId = Guid.NewGuid();

        await using var first = CreateHubConnection();
        await first.StartAsync();
        Assert.That(await HandshakeAsync(first, result, instanceId, 1), Is.EqualTo(SessionAnswer.Ok));
        Assert.That(await first.InvokeAsync<SessionAnswer>("Heartbeat"), Is.EqualTo(SessionAnswer.Ok));

        await using var second = CreateHubConnection();
        await second.StartAsync();
        Assert.That(await HandshakeAsync(second, result, instanceId, 2), Is.EqualTo(SessionAnswer.Ok));

        Assert.That(await first.InvokeAsync<SessionAnswer>("Heartbeat"), Is.EqualTo(SessionAnswer.Fenced));
        Assert.That(await second.InvokeAsync<SessionAnswer>("Heartbeat"), Is.EqualTo(SessionAnswer.Ok));
    }

    // A handshake that arrives after the connection that replaced its own must not take the worker's session back.
    [Test]
    public async Task Authenticate_LateHandshakeFromAnEarlierConnection_IsFenced()
    {
        var result = await RegisterWorkerAsync("late-handshake-worker");
        var instanceId = Guid.NewGuid();

        await using var later = CreateHubConnection();
        await later.StartAsync();
        Assert.That(await HandshakeAsync(later, result, instanceId, 2), Is.EqualTo(SessionAnswer.Ok));

        await using var earlier = CreateHubConnection();
        await earlier.StartAsync();
        Assert.That(await HandshakeAsync(earlier, result, instanceId, 1), Is.EqualTo(SessionAnswer.Fenced));
        Assert.That(await later.InvokeAsync<SessionAnswer>("Heartbeat"), Is.EqualTo(SessionAnswer.Ok));
    }

    private static async Task<SessionAnswer> HandshakeAsync(HubConnection connection, WorkerRegistrationResult worker, Guid instanceId, long counter)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        var challenge = await connection.InvokeAsync<AuthChallenge>("Connect", new WorkerConnect
        {
            WorkerId = worker.WorkerId,
            ProtocolHash = ProtocolVersion.Hash,
            Nonce = nonce,
            InstanceId = instanceId,
            ConnectionCounter = counter
        });

        using var key = new WorkerConfig { WorkerKey = worker.WorkerKey, ServerUrl = "unused" }.GetWorkerPrivateKey();
        var signature = key.SignData(HandshakeChallenge.Build(worker.WorkerId, instanceId, counter, nonce, challenge.Nonce), HashAlgorithmName.SHA256);
        return await connection.InvokeAsync<SessionAnswer>("Authenticate", new WorkerAuthenticate { Signature = signature });
    }

    // --- REST API Failures ---

    [Test]
    public async Task RegisterWorker_DuplicateName_Returns400()
    {
        await _fixture.HttpClient.PostAsJsonAsync("/api/workers", new { name = "duplicate-worker" });

        var response = await _fixture.HttpClient.PostAsJsonAsync("/api/workers", new { name = "duplicate-worker" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task RegisterWorker_EmptyName_Returns400()
    {
        var response = await _fixture.HttpClient.PostAsJsonAsync("/api/workers", new { name = "" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task RevokeWorker_NonExistent_Returns404()
    {
        var response = await _fixture.HttpClient.DeleteAsync($"/api/workers/{Guid.CreateVersion7()}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
