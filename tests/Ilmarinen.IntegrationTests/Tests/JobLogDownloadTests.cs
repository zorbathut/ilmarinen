using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Server.Controllers;
using Ilmarinen.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Linq;
using System.Net;
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
        var jobId = await JobLogSeed.CreateJobAsync(_fixture);
        await JobLogSeed.InsertChunksAsync(_fixture, jobId,
            (1, JobLogSeed.NdjsonLine("m", "=== step build ===\n") + JobLogSeed.NdjsonLine("o", "building\nlinking\n")),
            (2, JobLogSeed.NdjsonLine("e", "warning: ünused variable\n") + JobLogSeed.NdjsonLine("o", "done\n")));

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
        var jobId = await JobLogSeed.CreateJobAsync(_fixture);
        await JobLogSeed.InsertChunksAsync(_fixture, jobId, (3, JobLogSeed.NdjsonLine("o", "three\n")));
        await JobLogSeed.InsertChunksAsync(_fixture, jobId, (1, JobLogSeed.NdjsonLine("o", "one\n")), (2, JobLogSeed.NdjsonLine("o", "two\n")));
        await JobLogSeed.InsertChunksAsync(_fixture, jobId, (2, JobLogSeed.NdjsonLine("o", "two\n")));

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
        var jobId = await JobLogSeed.CreateJobAsync(_fixture);

        // Well under LogStreamService's flush thresholds, so it stays buffered until the download asks for it.
        var logStream = _fixture.Services.GetRequiredService<LogStreamService>();
        await logStream.ProcessChunkAsync(new LogChunk
        {
            JobId = jobId,
            SequenceNumber = 1,
            Content = JobLogSeed.NdjsonLine("o", "still buffered\n"),
            Timestamp = DateTime.UtcNow
        });

        var response = await _fixture.DownloadJobLogAsync(jobId);

        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("still buffered\n"));
    }

    [Test]
    public async Task DownloadLog_MoreChunksThanOnePage_ReturnsEveryChunk()
    {
        const int chunkCount = JobsController.DownloadPageSize + 50;

        var jobId = await JobLogSeed.CreateJobAsync(_fixture);
        var chunks = Enumerable.Range(1, chunkCount)
            .Select(i => (i, JobLogSeed.NdjsonLine("o", $"line {i}\n")))
            .ToArray();
        await JobLogSeed.InsertChunksAsync(_fixture, jobId, chunks);

        var response = await _fixture.DownloadJobLogAsync(jobId);
        var body = await response.Content.ReadAsStringAsync();

        var expected = string.Concat(Enumerable.Range(1, chunkCount).Select(i => $"line {i}\n"));
        Assert.That(body, Is.EqualTo(expected));
    }

    [Test]
    public async Task DownloadLog_JobWithoutLogs_ReturnsEmptyBody()
    {
        var jobId = await JobLogSeed.CreateJobAsync(_fixture);

        var response = await _fixture.DownloadJobLogAsync(jobId);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.Empty);
    }

    /// <summary>
    /// The raw view is the same bytes as the download, but shown in the browser rather than saved,
    /// which is how you search a log too big for the viewer's window.
    /// </summary>
    [Test]
    public async Task RawLog_ServesTheSameTextInline()
    {
        var jobId = await JobLogSeed.CreateJobAsync(_fixture);
        await JobLogSeed.InsertChunksAsync(_fixture, jobId, (1, JobLogSeed.NdjsonLine("o", "hello\n")));

        var response = await _fixture.HttpClient.GetAsync($"/api/jobs/{jobId}/logs/raw");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.ToString(), Is.EqualTo("text/plain; charset=utf-8"));
        Assert.That(response.Content.Headers.ContentDisposition?.DispositionType, Is.EqualTo("inline"));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo(await (await _fixture.DownloadJobLogAsync(jobId)).Content.ReadAsStringAsync()));
    }

    [Test]
    public async Task RawLog_UnknownJob_Returns404()
    {
        var response = await _fixture.HttpClient.GetAsync($"/api/jobs/{Guid.CreateVersion7()}/logs/raw");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task DownloadLog_UnknownJob_Returns404()
    {
        var response = await _fixture.DownloadJobLogAsync(Guid.CreateVersion7());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
