using Ilmarinen.Database;
using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
[Category("Integration")]
public class LogStreamServiceTests
{
    private IntegrationTestFixture _fixture = null!;
    private LogStreamService _logs = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new IntegrationTestFixture();
        await _fixture.SetupAsync();
        _logs = _fixture.Services.GetRequiredService<LogStreamService>();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _fixture.DisposeAsync();
    }

    // Completion drops the job's buffer, so a chunk that arrives afterwards — a worker replaying what it couldn't send
    // while disconnected — used to land in a fresh buffer that nothing would ever flush again.
    [Test]
    public async Task ChunksArrivingAfterTheJobCompleted_ArePersisted()
    {
        var jobId = await SubmitAsync();
        await _logs.ProcessChunkAsync(Chunk(jobId, 1, "while running"));

        await CompleteAsync(jobId);

        await _logs.ProcessChunkAsync(Chunk(jobId, 2, "replayed after completion"));
        await _logs.ProcessChunkAsync(Chunk(jobId, 3, "and the rest of the tail"));

        Assert.That(await StoredSequencesAsync(jobId), Is.EquivalentTo(new[] { 1, 2, 3 }));
    }

    // A running job's chunks still batch: the write-through above must not turn every chunk into its own round trip.
    [Test]
    public async Task ChunksWhileTheJobRuns_AreBatchedNotWrittenThrough()
    {
        var jobId = await SubmitAsync();

        await _logs.ProcessChunkAsync(Chunk(jobId, 1, "first"));
        await _logs.ProcessChunkAsync(Chunk(jobId, 2, "second"));

        Assert.That(await StoredSequencesAsync(jobId), Is.Empty);

        await CompleteAsync(jobId);
        Assert.That(await StoredSequencesAsync(jobId), Is.EquivalentTo(new[] { 1, 2 }));
    }

    private async Task<Guid> SubmitAsync()
    {
        return await _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = "/nonexistent/repo",
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });
    }

    private async Task CompleteAsync(Guid jobId)
    {
        using var scope = _fixture.Services.CreateScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<JobScheduler>();
        await scheduler.CompleteJobAsync(jobId, JobStatus.Success);
    }

    private async Task<IReadOnlyList<int>> StoredSequencesAsync(Guid jobId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        return await db.JobLogChunks
            .Where(c => c.JobId == jobId)
            .Select(c => c.SequenceNumber)
            .ToListAsync();
    }

    private static LogChunk Chunk(Guid jobId, int sequence, string text)
    {
        return new LogChunk
        {
            JobId = jobId,
            SequenceNumber = sequence,
            Content = text,
            Timestamp = DateTime.UtcNow
        };
    }
}
