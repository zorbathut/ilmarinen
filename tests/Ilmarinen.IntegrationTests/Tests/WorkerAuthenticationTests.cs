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
                ProtocolVersion = WorkerProtocol.Version,
                Nonce = RandomNumberGenerator.GetBytes(32),
                InstanceId = Guid.NewGuid(),
                ConnectionCounter = 1
            }));

        Assert.That(ex!.Message, Does.Contain("Unknown worker"));
    }

    [TestCase(WorkerProtocol.MinSupportedVersion - 1)]
    [TestCase(WorkerProtocol.Version + 1)]
    public async Task Connect_AVersionOutsideTheSupportedRange_Throws(int version)
    {
        var result = await RegisterWorkerAsync("version-mismatch-worker");

        await using var connection = CreateHubConnection();
        await connection.StartAsync();

        var ex = Assert.ThrowsAsync<HubException>(async () =>
            await connection.InvokeAsync<AuthChallenge>("Connect", new WorkerConnect
            {
                WorkerId = result.WorkerId,
                ProtocolVersion = version,
                Nonce = RandomNumberGenerator.GetBytes(32),
                InstanceId = Guid.NewGuid(),
                ConnectionCounter = 1
            }));

        Assert.That(ex!.Message, Does.Contain($"v{version}"));
    }

    // Two builds claiming the same version but differing in shape are a slip in recording versions, not a reason to strand a worker: the server only logs it.
    [Test]
    public async Task Connect_ASupportedVersionWithAnotherShape_IsStillAccepted()
    {
        var result = await RegisterWorkerAsync("drifted-worker");

        await using var connection = CreateHubConnection();
        await connection.StartAsync();

        var challenge = await connection.InvokeAsync<AuthChallenge>("Connect", new WorkerConnect
        {
            WorkerId = result.WorkerId,
            ProtocolVersion = WorkerProtocol.Version,
            ProtocolHash = "000000000000",
            Nonce = RandomNumberGenerator.GetBytes(32),
            InstanceId = Guid.NewGuid(),
            ConnectionCounter = 1
        });

        Assert.That(challenge.Nonce, Is.Not.Empty);
    }

    // A worker built before the protocol had versions sends what its WorkerConnect had then, over the wire as it would: it must be told its version is unsupported, rather than fail to bind, so its log says what to do.
    [Test]
    public async Task Connect_FromAWorkerBuiltBeforeVersions_IsRejectedForItsVersion()
    {
        var result = await RegisterWorkerAsync("pre-version-worker");

        await using var connection = CreateHubConnection();
        await connection.StartAsync();

        var legacy = new
        {
            workerId = result.WorkerId,
            protocolHash = "BF2088CE031C",
            nonce = RandomNumberGenerator.GetBytes(32),
            instanceId = Guid.NewGuid(),
            connectionCounter = 1L,
            bundleHash = (string?)null
        };
        var ex = Assert.ThrowsAsync<HubException>(async () => await connection.InvokeCoreAsync<AuthChallenge>("Connect", [legacy]));

        Assert.That(ex!.Message, Does.Contain("v0"));
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
            ProtocolVersion = WorkerProtocol.Version,
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
                ProtocolVersion = WorkerProtocol.Version,
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

    // What WorkerShapeDiff's rules rest on, checked over a real connection: a hub method's argument binds despite a member it doesn't know, an enum value it doesn't know, and nullable members left out, so a message from a newer or older worker reaches the hub instead of failing to bind.
    [Test]
    public async Task AHubArgument_BindsDespiteUnknownMembersAndValues_AndMissingNullableOnes()
    {
        var result = await RegisterWorkerAsync("tolerant-binding-worker");
        await using var connection = CreateHubConnection();
        await connection.StartAsync();
        Assert.That(await HandshakeAsync(connection, result, Guid.NewGuid(), 1), Is.EqualTo(SessionAnswer.Ok));

        var state = new
        {
            seq = 1L,
            activity = 99,
            somethingNewer = "ignored"
        };
        var answer = await connection.InvokeCoreAsync<SessionAnswer>("ReportState", [state]);

        Assert.That(answer, Is.EqualTo(SessionAnswer.NotResumed), "bound and answered: a state before Resume is refused by the hub itself, not by the binder");
    }

    private static async Task<SessionAnswer> HandshakeAsync(HubConnection connection, WorkerRegistrationResult worker, Guid instanceId, long counter)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        var challenge = await connection.InvokeAsync<AuthChallenge>("Connect", new WorkerConnect
        {
            WorkerId = worker.WorkerId,
            ProtocolVersion = WorkerProtocol.Version,
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
