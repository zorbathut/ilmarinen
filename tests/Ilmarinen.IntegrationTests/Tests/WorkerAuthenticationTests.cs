using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using NUlid;
using NUnit.Framework;

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
                options.PayloadSerializerOptions.Converters.Add(new UlidJsonConverter());
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
                WorkerId = Ulid.NewUlid(),
                BuildId = BuildInfo.GitCommit,
                Nonce = RandomNumberGenerator.GetBytes(32)
            }));

        Assert.That(ex!.Message, Does.Contain("Unknown worker"));
    }

    [Test]
    public async Task Connect_BuildMismatch_Throws()
    {
        var result = await RegisterWorkerAsync("build-mismatch-worker");

        await using var connection = CreateHubConnection();
        await connection.StartAsync();

        var ex = Assert.ThrowsAsync<HubException>(async () =>
            await connection.InvokeAsync<AuthChallenge>("Connect", new WorkerConnect
            {
                WorkerId = result.WorkerId,
                BuildId = "wrong-build-id",
                Nonce = RandomNumberGenerator.GetBytes(32)
            }));

        Assert.That(ex!.Message, Does.Contain("Build mismatch"));
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
        var challenge = await connection.InvokeAsync<AuthChallenge>("Connect", new WorkerConnect
        {
            WorkerId = result.WorkerId,
            BuildId = BuildInfo.GitCommit,
            Nonce = workerNonce
        });

        // Sign with a completely different key
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var challengeData = Encoding.UTF8.GetBytes(
            $"{result.WorkerId}:{Convert.ToBase64String(workerNonce)}:{Convert.ToBase64String(challenge.Nonce)}");
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
                BuildId = BuildInfo.GitCommit,
                Nonce = RandomNumberGenerator.GetBytes(32)
            }));

        Assert.That(ex!.Message, Does.Contain("Unknown worker"));
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
        var response = await _fixture.HttpClient.DeleteAsync($"/api/workers/{Ulid.NewUlid()}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
