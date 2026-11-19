using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Metrics;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>A real worker sampling a real job: a step that burns CPU beside a service it started, and what reaches the server about it.</summary>
[TestFixture]
[Category("Integration")]
public class WorkerJobMetricsTests
{
    private IntegrationTestFixture _fixture = null!;
    private TestGitRepository _repo = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new IntegrationTestFixture { WorkerMetricsInterval = TimeSpan.FromMilliseconds(500) };
        await _fixture.SetupAsync();
        await _fixture.StartWorkerAsync();

        _repo = new TestGitRepository();
        _repo.AddFile("pipeline.csx", """
            Step("burn")
                .Image("docker:cli")
                .Run(async ctx => {
                    await ctx.StartService("redis:alpine", "cache");
                    await ctx.Shell("timeout 3 sh -c 'while :; do :; done' || true");
                });
            """);
        _repo.Commit("Add pipeline");
    }

    [TearDown]
    public async Task TearDown()
    {
        _repo.Dispose();
        await _fixture.DisposeAsync();
    }

    [Test]
    public async Task AJobsMetrics_ShowItsStep_ItsContainers_AndTheCpuTheStepBurned()
    {
        var jobId = await _fixture.SubmitJobAsync(new JobSubmission { RepoUrl = _repo.Url, Ref = "master", ScriptPath = "pipeline.csx" });
        var job = await _fixture.WaitForJobCompletionAsync(jobId);
        Assert.That(job.Status, Is.EqualTo(JobStatus.Success));

        var records = await WaitForRecordsAsync(jobId, rs => rs.OfType<StepRecord>().Any(s => s.Event == "end"));

        Assert.That(records.OfType<HostRecord>(), Is.Not.Empty);
        Assert.That(records.OfType<StepRecord>().Select(s => (s.Name, s.Event)), Is.EqualTo(new[] { ("burn", "start"), ("burn", "end") }));

        var containers = records.OfType<ContainerRecord>().ToList();
        var stepContainer = containers.Single(c => c.Role == "step");
        Assert.That(containers.Where(c => c.Role == "other").Select(c => c.Image), Has.Some.Contains("redis"), "the service is on the job's network too");

        var samples = records.OfType<SampleRecord>().ToList();
        Assert.That(samples.Where(s => s.Step == "burn"), Is.Not.Empty);
        // Presence only: other tests run beside this one, so the host's figures are theirs as much as this job's.
        Assert.That(samples.Select(s => s.Host).Where(h => h != null), Is.Not.Empty);
        var stepCpu = samples.SelectMany(s => s.Containers).Where(c => c.Id == stepContainer.Id).Max(c => c.CpuSecondsTotal);
        Assert.That(stepCpu, Is.GreaterThan(0.5), "three seconds of a busy loop");

        var steps = await _fixture.HttpClient.GetFromJsonAsync<List<JobStepInfo>>($"/api/jobs/{jobId}/steps", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.That(steps!.Single().CpuSeconds, Is.GreaterThan(0.5));
        Assert.That(steps.Single().Status, Is.EqualTo("Success"));
    }

    private async Task<List<MetricsRecord>> WaitForRecordsAsync(Guid jobId, Func<List<MetricsRecord>, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var body = await _fixture.HttpClient.GetStringAsync($"/api/jobs/{jobId}/metrics");
            var records = body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => MetricsRecords.Parse(l)!).ToList();
            if (done(records) || DateTime.UtcNow > deadline)
            {
                return records;
            }
            await Task.Delay(200);
        }
    }
}
