using Ilmarinen.Database.Entities;
using Ilmarinen.Database;
using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Net.Http.Json;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>GET /api/jobs/queue/estimate against real history: the forecast fed by the database and the live sessions.</summary>
[TestFixture]
[Category("Integration")]
public class QueueEstimateEndpointTests
{
    private const string Repo = "https://example.invalid/forecast.git";
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
    public async Task ARunningJobAndOneQueuedBehindIt_DrainAfterBoth()
    {
        var worker = await DirectedWorker.ReadyAsync(_fixture, "forecast-worker", "conn-forecast");

        // History: a setup run, then two 10-minute runs, so this worker's estimate for the script is 10 minutes.
        var clock = DateTime.UtcNow.AddDays(-1);
        foreach (var minutes in new[] { 40, 10, 10 })
        {
            await AddJobAsync(JobStatus.Success, worker.WorkerId, clock, clock.AddMinutes(minutes));
            clock = clock.AddMinutes(minutes + 1);
        }
        // Running for two minutes, and one more waiting behind it.
        await AddJobAsync(JobStatus.Running, worker.WorkerId, DateTime.UtcNow.AddMinutes(-2), null);
        await AddJobAsync(JobStatus.Queued, null, null, null);

        var estimate = await _fixture.HttpClient.GetFromJsonAsync<QueueEstimate>("/api/jobs/queue/estimate");

        Assert.That(estimate!.RunningJobs, Is.EqualTo(1));
        Assert.That(estimate.QueuedJobs, Is.EqualTo(1));
        Assert.That(estimate.AvailableWorkers, Is.EqualTo(1));
        Assert.That(estimate.ConnectedWorkers, Is.EqualTo(1));
        Assert.That(estimate.UnestimatedJobs, Is.Zero);
        Assert.That(estimate.UnsatisfiableJobs, Is.Zero);
        Assert.That(estimate.EstimatedDrainSeconds, Is.InRange(17 * 60, 18 * 60), "eight minutes left on the running job, then ten for the queued one");
    }

    // The worker that will take the job has never run it; another worker's history is what says how long it takes.
    [Test]
    public async Task AQueuedJobForAWorkerNewToIt_IsEstimatedFromOtherWorkers()
    {
        var newcomer = await DirectedWorker.ReadyAsync(_fixture, "forecast-newcomer", "conn-forecast-newcomer");
        var veteran = await DirectedWorker.RegisterAsync(_fixture, "forecast-veteran");
        var clock = DateTime.UtcNow.AddDays(-1);
        foreach (var minutes in new[] { 12, 12, 12 })
        {
            await AddJobAsync(JobStatus.Success, veteran.WorkerId, clock, clock.AddMinutes(minutes));
            clock = clock.AddMinutes(minutes + 1);
        }
        await AddJobAsync(JobStatus.Queued, null, null, null);

        var estimate = await _fixture.HttpClient.GetFromJsonAsync<QueueEstimate>("/api/jobs/queue/estimate");

        Assert.That(estimate!.AvailableWorkers, Is.EqualTo(1), "only the newcomer is connected");
        Assert.That(estimate.UnestimatedJobs, Is.Zero);
        Assert.That(estimate.EstimatedDrainSeconds, Is.EqualTo(12 * 60));
    }

    [Test]
    public async Task NoWorkers_TheQueueIsUnsatisfiable()
    {
        await AddJobAsync(JobStatus.Queued, null, null, null);

        var estimate = await _fixture.HttpClient.GetFromJsonAsync<QueueEstimate>("/api/jobs/queue/estimate");

        Assert.That(estimate!.QueuedJobs, Is.EqualTo(1));
        Assert.That(estimate.UnsatisfiableJobs, Is.EqualTo(1));
        Assert.That(estimate.AvailableWorkers, Is.Zero);
        Assert.That(estimate.EstimatedDrainSeconds, Is.Zero);
    }

    private async Task AddJobAsync(JobStatus status, Guid? workerId, DateTime? started, DateTime? completed)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        db.Jobs.Add(new Job
        {
            Id = Guid.CreateVersion7(),
            Status = status,
            RepoUrl = Repo,
            Ref = "master",
            ScriptPath = "pipeline.csx",
            WorkerId = workerId,
            CreatedAt = started ?? DateTime.UtcNow,
            StartedAt = started,
            CompletedAt = completed
        });
        await db.SaveChangesAsync();
    }
}
