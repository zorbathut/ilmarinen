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

    private async Task<WorkerPriority> GetPriorityAsync(Guid workerId)
    {
        var response = await _fixture.HttpClient.GetAsync("/api/workers");
        response.EnsureSuccessStatusCode();

        var workers = await response.Content.ReadFromJsonAsync<List<WorkerView>>(JsonOptions);
        return workers!.Single(w => w.Id == workerId).Priority;
    }

    [Test]
    public async Task GetWorkers_NewlyRegisteredWorker_HasMediumPriority()
    {
        var workerId = await RegisterWorkerAsync("priority-default-worker");

        Assert.That(await GetPriorityAsync(workerId), Is.EqualTo(WorkerPriority.Medium));
    }

    [Test]
    public async Task UpdateWorker_ValidPriority_PersistsIt()
    {
        var workerId = await RegisterWorkerAsync("priority-update-worker");

        var response = await _fixture.HttpClient.PutAsJsonAsync(
            $"/api/workers/{workerId}",
            new { priority = WorkerPriority.High });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(await GetPriorityAsync(workerId), Is.EqualTo(WorkerPriority.High));
    }

    [Test]
    public async Task UpdateWorker_UnknownWorker_ReturnsNotFound()
    {
        var response = await _fixture.HttpClient.PutAsJsonAsync(
            $"/api/workers/{Guid.NewGuid()}",
            new { priority = WorkerPriority.Low });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task UpdateWorker_OutOfRangePriority_ReturnsBadRequest()
    {
        var workerId = await RegisterWorkerAsync("priority-out-of-range-worker");

        // System.Text.Json happily deserializes any int into an enum, so an unguarded endpoint would persist this and sort the worker above High.
        var content = new StringContent("{\"priority\":99}", Encoding.UTF8, "application/json");
        var response = await _fixture.HttpClient.PutAsync($"/api/workers/{workerId}", content);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await GetPriorityAsync(workerId), Is.EqualTo(WorkerPriority.Medium),
            "a rejected update must not change the stored priority");
    }
}
