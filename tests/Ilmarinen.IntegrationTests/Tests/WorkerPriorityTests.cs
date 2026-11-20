using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// The REST surface for worker priority — the default, the round trip, and the two ways a caller can get it wrong. What priority actually *does* is covered by JobSchedulingTests.
/// </summary>
[TestFixture]
[Category("Integration")]
public class WorkerPriorityTests
{
    private IntegrationTestFixture _fixture = null!;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

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

    private async Task<Guid> RegisterWorkerAsync(string name)
    {
        using var scope = _fixture.Services.CreateScope();
        var registration = scope.ServiceProvider.GetRequiredService<WorkerRegistrationService>();
        var result = await registration.RegisterWorkerAsync(name);
        return result.WorkerId;
    }

    private async Task<string> GetPriorityAsync(Guid workerId)
    {
        var response = await _fixture.HttpClient.GetAsync("/api/workers");
        response.EnsureSuccessStatusCode();

        var workers = await response.Content.ReadFromJsonAsync<List<WorkerView>>(JsonOptions);
        return workers!.Single(w => w.Id == workerId).Priority.Name;
    }

    [Test]
    public async Task GetWorkers_ListsThemByName_NotByWhenTheyWereLastSeen()
    {
        await RegisterWorkerAsync("order-b");
        await RegisterWorkerAsync("order-c");
        await RegisterWorkerAsync("order-a");

        var response = await _fixture.HttpClient.GetAsync("/api/workers");
        response.EnsureSuccessStatusCode();
        var workers = await response.Content.ReadFromJsonAsync<List<WorkerView>>(JsonOptions);

        Assert.That(workers!.Select(w => w.Name).Where(n => n.StartsWith("order-")), Is.EqualTo(new[] { "order-a", "order-b", "order-c" }));
    }

    [Test]
    public async Task GetWorkers_NewlyRegisteredWorker_HasMediumPriority()
    {
        var workerId = await RegisterWorkerAsync("priority-default-worker");

        Assert.That(await GetPriorityAsync(workerId), Is.EqualTo(StandardPriority.Medium));
    }

    [Test]
    public async Task UpdateWorker_ValidPriority_PersistsIt()
    {
        var workerId = await RegisterWorkerAsync("priority-update-worker");

        var response = await _fixture.HttpClient.PutAsJsonAsync(
            $"/api/workers/{workerId}",
            new { priority = StandardPriority.High });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(await GetPriorityAsync(workerId), Is.EqualTo(StandardPriority.High));
    }

    [Test]
    public async Task UpdateWorker_UnknownWorker_ReturnsNotFound()
    {
        var response = await _fixture.HttpClient.PutAsJsonAsync(
            $"/api/workers/{Guid.NewGuid()}",
            new { priority = StandardPriority.Low });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task UpdateWorker_UnknownPriority_ReturnsBadRequest()
    {
        var workerId = await RegisterWorkerAsync("priority-unknown-worker");

        var content = new StringContent("{\"priority\":\"Ultra\"}", Encoding.UTF8, "application/json");
        var response = await _fixture.HttpClient.PutAsync($"/api/workers/{workerId}", content);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await GetPriorityAsync(workerId), Is.EqualTo(StandardPriority.Medium),
            "a rejected update must not change the stored priority");
    }
}
