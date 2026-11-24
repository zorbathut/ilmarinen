using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// A job's log as one run writes it, driven through the hub: the server's own lines among the worker's take nobody's number, output naming another run of the job is dropped, and a worker that names no run (before protocol v2) is taken at its word.
/// </summary>
[TestFixture]
[Category("Integration")]
public class JobLogRunTests
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
    public async Task AServerLine_MidRun_HidesNothingTheWorkerSendsAfterIt()
    {
        var worker = await DirectedWorker.ReadyAsync(_fixture, "log-line-worker", "conn-log-line");
        var (jobId, assignment) = await StartAsync(worker);
        await SendAsync(worker, jobId, assignment, 1, "one");
        await SendAsync(worker, jobId, assignment, 2, "two");

        await _fixture.Services.GetRequiredService<LogStreamService>().AppendLineAsync(jobId, "from the server");
        await SendAsync(worker, jobId, assignment, 3, "three");
        await SendAsync(worker, jobId, assignment, 4, "four");

        var lines = await ReadLogAsync(jobId);
        Assert.That(lines.Count, Is.EqualTo(5), string.Join("\n", lines));
        Assert.That(lines[0], Does.Contain("one"));
        Assert.That(lines[1], Does.Contain("two"));
        Assert.That(lines[2], Does.Contain("from the server"));
        Assert.That(lines[3], Does.Contain("three"));
        Assert.That(lines[4], Does.Contain("four"));
    }

    [Test]
    public async Task OutputNamingAnotherRun_IsDropped()
    {
        var worker = await DirectedWorker.ReadyAsync(_fixture, "log-stale-worker", "conn-log-stale");
        var (jobId, assignment) = await StartAsync(worker);
        var other = Guid.NewGuid();

        await SendAsync(worker, jobId, assignment, 1, "this run");
        await SendAsync(worker, jobId, other, 2, "another run");
        await worker.Hub.StreamMetrics(new JobMetricsChunk { JobId = jobId, AssignmentId = other, SequenceNumber = 0, Content = "{\"other\":1}\n", Timestamp = DateTime.UtcNow });
        await worker.Hub.StreamMetrics(new JobMetricsChunk { JobId = jobId, AssignmentId = assignment, SequenceNumber = 1, Content = "{\"this\":1}\n", Timestamp = DateTime.UtcNow });

        var lines = await ReadLogAsync(jobId);
        Assert.That(lines, Has.Count.EqualTo(1));
        Assert.That(lines[0], Does.Contain("this run"));
        var metrics = await MetricsAsync(jobId);
        Assert.That(metrics, Does.Contain("this"));
        Assert.That(metrics, Does.Not.Contain("other"));
    }

    [Test]
    public async Task OutputNamingNoRun_FromAWorkerBeforeV2_CountsAsTheCurrentRuns()
    {
        var worker = await DirectedWorker.ReadyAsync(_fixture, "log-v1-worker", "conn-log-v1", protocolVersion: 1);
        var (jobId, _) = await StartAsync(worker);

        await worker.Hub.StreamLogs(new LogChunk { JobId = jobId, SequenceNumber = 1, Content = Line("from a v1 worker"), Timestamp = DateTime.UtcNow });
        await worker.Hub.StreamMetrics(new JobMetricsChunk { JobId = jobId, SequenceNumber = 0, Content = "{\"v1\":1}\n", Timestamp = DateTime.UtcNow });

        Assert.That((await ReadLogAsync(jobId)).Single(), Does.Contain("from a v1 worker"));
        Assert.That(await MetricsAsync(jobId), Does.Contain("v1"));
    }

    private async Task<(Guid JobId, Guid AssignmentId)> StartAsync(DirectedWorker worker)
    {
        var jobId = await _fixture.Services.GetRequiredService<JobScheduler>().EnqueueJobAsync(new JobSubmission
        {
            RepoUrl = "/nonexistent/repo",
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });
        var assignment = (await worker.SentAsync()).Assignments.Single(a => a.Id == jobId).AssignmentId;
        await worker.ReportAsync(worker.Running(jobId, assignment));
        return (jobId, assignment);
    }

    private static string Line(string text)
    {
        return System.Text.Json.JsonSerializer.Serialize(new { t = "o", d = text, ts = 0 }) + "\n";
    }

    private static async Task SendAsync(DirectedWorker worker, Guid jobId, Guid assignmentId, int sequence, string text)
    {
        await worker.Hub.StreamLogs(new LogChunk { JobId = jobId, AssignmentId = assignmentId, SequenceNumber = sequence, Content = Line(text), Timestamp = DateTime.UtcNow });
    }

    /// <summary>The job's log as a reader pages through it: in sequence order, a repeated number read once.</summary>
    private async Task<IReadOnlyList<string>> ReadLogAsync(Guid jobId)
    {
        await _fixture.Services.GetRequiredService<LogStreamService>().FlushJobAsync(jobId);
        using var scope = _fixture.Services.CreateScope();
        var chunks = await scope.ServiceProvider.GetRequiredService<JobLogRepository>().GetChunksAsync(jobId, 0, 1000);
        var seen = new HashSet<int>();
        return chunks.Where(c => seen.Add(c.SequenceNumber)).Select(c => c.Content.Trim()).ToList();
    }

    private async Task<string> MetricsAsync(Guid jobId)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<JobMetricsRepository>().GetContentAsync(jobId);
    }
}
