using Ilmarinen.Database.Entities;
using Ilmarinen.Database;
using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Controllers;
using Ilmarinen.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// Covers GET /api/jobs/{id}/logs/download: the whole stored log, decoded from NDJSON to plain
/// text, regardless of how the chunks landed in the table.
/// </summary>
[TestFixture]
[Category("Integration")]
public class JobLogDownloadTests
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
    public async Task DownloadLog_ReturnsWholeLogAsPlainTextAttachment()
    {
        var jobId = await SubmitQueuedJobAsync();
        await InsertChunksAsync(jobId,
            (1, NdjsonLine("m", "=== step build ===\n") + NdjsonLine("o", "building\nlinking\n")),
            (2, NdjsonLine("e", "warning: ünused variable\n") + NdjsonLine("o", "done\n")));

        var response = await _fixture.DownloadJobLogAsync(jobId);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.ToString(), Is.EqualTo("text/plain; charset=utf-8"));
        Assert.That(response.Content.Headers.ContentDisposition?.DispositionType, Is.EqualTo("attachment"));
        Assert.That(response.Content.Headers.ContentDisposition?.FileName?.Trim('"'), Is.EqualTo($"job-{jobId}.log"));

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.That(Encoding.UTF8.GetString(bytes), Is.EqualTo("=== step build ===\nbuilding\nlinking\nwarning: ünused variable\ndone\n"));
        Assert.That(bytes.Take(3), Is.Not.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }), "log files must not carry a UTF-8 BOM");
    }

    /// <summary>
    /// A worker replaying its buffer after a reconnect stores chunks out of sequence order, and can
    /// store the same sequence twice when a send only appeared to fail.
    /// </summary>
    [Test]
    public async Task DownloadLog_OutOfOrderAndDuplicateChunks_AreOrderedAndDeduplicated()
    {
        var jobId = await SubmitQueuedJobAsync();
        await InsertChunksAsync(jobId, (3, NdjsonLine("o", "three\n")));
        await InsertChunksAsync(jobId, (1, NdjsonLine("o", "one\n")), (2, NdjsonLine("o", "two\n")));
        await InsertChunksAsync(jobId, (2, NdjsonLine("o", "two\n")));

        var response = await _fixture.DownloadJobLogAsync(jobId);

        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("one\ntwo\nthree\n"));
    }

    /// <summary>
    /// A running job's newest chunks are still in the server's persistence buffer, which the
    /// download has to flush before reading the table.
    /// </summary>
    [Test]
    public async Task DownloadLog_ChunksStillBufferedForPersistence_AreIncluded()
    {
        var jobId = await SubmitQueuedJobAsync();

        // Well under LogStreamService's flush thresholds, so it stays buffered until the download asks for it.
        var logStream = _fixture.Services.GetRequiredService<LogStreamService>();
        await logStream.ProcessChunkAsync(new LogChunk
        {
            JobId = jobId,
            SequenceNumber = 1,
            Content = NdjsonLine("o", "still buffered\n"),
            Timestamp = DateTime.UtcNow
        });

        var response = await _fixture.DownloadJobLogAsync(jobId);

        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("still buffered\n"));
    }

    [Test]
    public async Task DownloadLog_MoreChunksThanOnePage_ReturnsEveryChunk()
    {
        const int chunkCount = JobsController.DownloadPageSize + 50;

        var jobId = await SubmitQueuedJobAsync();
        var chunks = Enumerable.Range(1, chunkCount)
            .Select(i => (i, NdjsonLine("o", $"line {i}\n")))
            .ToArray();
        await InsertChunksAsync(jobId, chunks);

        var response = await _fixture.DownloadJobLogAsync(jobId);
        var body = await response.Content.ReadAsStringAsync();

        var expected = string.Concat(Enumerable.Range(1, chunkCount).Select(i => $"line {i}\n"));
        Assert.That(body, Is.EqualTo(expected));
    }

    [Test]
    public async Task DownloadLog_JobWithoutLogs_ReturnsEmptyBody()
    {
        var jobId = await SubmitQueuedJobAsync();

        var response = await _fixture.DownloadJobLogAsync(jobId);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.Empty);
    }

    [Test]
    public async Task DownloadLog_UnknownJob_Returns404()
    {
        var response = await _fixture.DownloadJobLogAsync(Guid.CreateVersion7());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    private async Task<Guid> SubmitQueuedJobAsync()
    {
        // No worker is running, so the job just sits in the queue — nothing needs to execute for its logs to be readable.
        return await _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = "https://example.invalid/repo.git",
            Ref = "master",
            ScriptPath = "pipeline.csx",
            GitTokenMode = GitTokenMode.None
        });
    }

    private async Task InsertChunksAsync(Guid jobId, params (int Sequence, string Content)[] chunks)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

        foreach (var (sequence, content) in chunks)
        {
            db.JobLogChunks.Add(new JobLogChunk
            {
                Id = Guid.CreateVersion7(),
                JobId = jobId,
                SequenceNumber = sequence,
                Content = content,
                Timestamp = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();
    }

    private static string NdjsonLine(string type, string data)
    {
        return JsonSerializer.Serialize(new { t = type, d = data, ts = 0 }) + "\n";
    }
}
