using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Metrics;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>Metrics chunks arriving at the hub: kept for the worker's own job only, read back once each in order, and summarised per step.</summary>
[TestFixture]
[Category("Integration")]
public class JobMetricsHubTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-19T12:00:00Z");
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

    private async Task<(DirectedWorker Owner, Guid JobId)> RunningJobAsync()
    {
        var owner = await DirectedWorker.RegisterAsync(_fixture, "metrics-owner");
        await owner.ConnectAsync("conn-metrics-owner");
        var jobId = await _fixture.Services.GetRequiredService<JobScheduler>().EnqueueJobAsync(new JobSubmission
        {
            RepoUrl = "https://example.invalid/repo.git",
            Ref = "master",
            ScriptPath = "pipeline.csx",
            GitTokenMode = GitTokenMode.None
        });
        await _fixture.StartJobOnWorkerAsync(jobId, owner);
        return (owner, jobId);
    }

    private static JobMetricsChunk Chunk(Guid jobId, int sequence, params MetricsRecord[] records)
    {
        var host = new HostRecord { Ts = T0, Hostname = "fake", CpuCount = 2 };
        return new JobMetricsChunk
        {
            JobId = jobId,
            SequenceNumber = sequence,
            Content = string.Concat(new MetricsRecord[] { host }.Concat(records).Select(r => MetricsRecords.Serialize(r) + "\n")),
            Timestamp = DateTime.UtcNow
        };
    }

    private async Task<List<MetricsRecord>> GetRecordsAsync(Guid jobId)
    {
        var body = await _fixture.HttpClient.GetStringAsync($"/api/jobs/{jobId}/metrics");
        return body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => MetricsRecords.Parse(l)!).ToList();
    }

    [Test]
    public async Task ChunksFromTheJobsWorker_AreKept_InOrder_AndARepeatedOneOnce()
    {
        var (owner, jobId) = await RunningJobAsync();
        var first = Chunk(jobId, 0, new SampleRecord { Ts = T0, IntervalSeconds = 0, Phase = "prepare" });
        var second = Chunk(jobId, 1, new SampleRecord { Ts = T0.AddSeconds(2), IntervalSeconds = 2, Phase = "pipeline" });

        Assert.That(await owner.Hub.StreamMetrics(second), Is.EqualTo(SessionAnswer.Ok));
        Assert.That(await owner.Hub.StreamMetrics(first), Is.EqualTo(SessionAnswer.Ok));
        Assert.That(await owner.Hub.StreamMetrics(second), Is.EqualTo(SessionAnswer.Ok), "re-sent after a send that only seemed to fail");

        var samples = (await GetRecordsAsync(jobId)).OfType<SampleRecord>().ToList();
        Assert.That(samples.Select(s => s.Phase), Is.EqualTo(new[] { "prepare", "pipeline" }));
    }

    [Test]
    public async Task ChunksFromAnotherWorker_AreIgnored()
    {
        var (_, jobId) = await RunningJobAsync();
        var other = await DirectedWorker.RegisterAsync(_fixture, "metrics-other");
        await other.ConnectAsync("conn-metrics-other");

        Assert.That(await other.Hub.StreamMetrics(Chunk(jobId, 0, new SampleRecord { Ts = T0, IntervalSeconds = 0, Phase = "prepare" })), Is.EqualTo(SessionAnswer.Ok));

        Assert.That(await GetRecordsAsync(jobId), Is.Empty);
    }

    [Test]
    public async Task AStepsSummary_IsStored_AndARepeatedChunkDoesntRepeatIt()
    {
        var (owner, jobId) = await RunningJobAsync();
        var summary = new StepSummaryRecord { Ts = T0.AddSeconds(3), Index = 0, Name = "build", StartedAt = T0, Status = "Success", Image = "alpine", CpuSeconds = 3, PeakMemBytes = 64 };
        var chunk = Chunk(jobId, 0, new StepRecord { Ts = T0.AddSeconds(3), Index = 0, Name = "build", Event = "end", Status = "Success" }, summary);

        await owner.Hub.StreamMetrics(chunk);
        await owner.Hub.StreamMetrics(chunk);

        var steps = await _fixture.HttpClient.GetFromJsonAsync<List<JobStepInfo>>($"/api/jobs/{jobId}/steps", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.That(steps!.Select(s => (s.Name, s.Status)), Is.EqualTo(new[] { ("build", "Success") }));
        Assert.That(steps[0].CpuSeconds, Is.EqualTo(3).Within(1e-9));
        Assert.That(steps[0].PeakMemBytes, Is.EqualTo(64));
        Assert.That(steps[0].EndedAt, Is.EqualTo(T0.AddSeconds(3).UtcDateTime));
    }

    [Test]
    public async Task AnUnknownJob_IsNotFound()
    {
        var metrics = await _fixture.HttpClient.GetAsync($"/api/jobs/{Guid.NewGuid()}/metrics");
        var steps = await _fixture.HttpClient.GetAsync($"/api/jobs/{Guid.NewGuid()}/steps");

        Assert.That(metrics.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(steps.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
