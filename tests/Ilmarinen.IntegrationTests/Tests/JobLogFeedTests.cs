using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using Ilmarinen.Server;
using Ilmarinen.Server.Controllers;
using Ilmarinen.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// Covers GET /api/jobs/{id}/logs/chunks, the paging feed the full-screen viewer reads: stored
/// NDJSON served verbatim, windowed by sequence in either direction.
/// </summary>
[TestFixture]
[Category("Integration")]
public class JobLogFeedTests
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
    public async Task Feed_TailRequest_ReturnsTheNewestChunksAscending()
    {
        var jobId = await SeedNumberedChunksAsync(10);

        var response = await FeedAsync(jobId, "?limit=3");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/x-ndjson"));
        Assert.That(await DecodeAsync(response), Is.EqualTo("line 8\nline 9\nline 10\n"));
        Assert.That(SequenceHeader(response, "X-Log-First-Sequence"), Is.EqualTo(8));
        Assert.That(SequenceHeader(response, "X-Log-Last-Sequence"), Is.EqualTo(10));
    }

    [Test]
    public async Task Feed_LogShorterThanLimit_ReturnsEverything()
    {
        var jobId = await SeedNumberedChunksAsync(3);

        var response = await FeedAsync(jobId, "?limit=10");

        Assert.That(await DecodeAsync(response), Is.EqualTo("line 1\nline 2\nline 3\n"));
        Assert.That(SequenceHeader(response, "X-Log-First-Sequence"), Is.EqualTo(1));
    }

    [Test]
    public async Task Feed_Before_ExcludesTheBoundAndReturnsEarlierChunks()
    {
        var jobId = await SeedNumberedChunksAsync(10);

        var response = await FeedAsync(jobId, "?before=5&limit=2");

        Assert.That(await DecodeAsync(response), Is.EqualTo("line 3\nline 4\n"));
        Assert.That(SequenceHeader(response, "X-Log-Last-Sequence"), Is.EqualTo(4), "before= must exclude its own bound");
    }

    [Test]
    public async Task Feed_After_ExcludesTheBoundAndReturnsNewerChunks()
    {
        var jobId = await SeedNumberedChunksAsync(10);

        var response = await FeedAsync(jobId, "?after=5&limit=2");

        Assert.That(await DecodeAsync(response), Is.EqualTo("line 6\nline 7\n"));
        Assert.That(SequenceHeader(response, "X-Log-First-Sequence"), Is.EqualTo(6), "after= must exclude its own bound");
    }

    /// <summary>
    /// Every poll on an idle running job takes this path, and a client that read a zero here would
    /// clobber the window bookkeeping it is about to reuse.
    /// </summary>
    [Test]
    public async Task Feed_EmptyResult_OmitsTheSequenceHeaders()
    {
        var jobId = await SeedNumberedChunksAsync(10);

        var response = await FeedAsync(jobId, "?after=10");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.Empty);
        Assert.That(response.Headers.Contains("X-Log-First-Sequence"), Is.False);
        Assert.That(response.Headers.Contains("X-Log-Last-Sequence"), Is.False);
        Assert.That(response.Headers.Contains("X-Job-Status"), Is.True);
    }

    /// <summary>
    /// A quiet build step leaves the newest chunk in the server's persistence buffer indefinitely,
    /// because nothing evaluates the flush thresholds until the next chunk arrives.
    /// </summary>
    [Test]
    public async Task Feed_ChunkStillBufferedForPersistence_AppearsOnAPoll()
    {
        var jobId = await SeedNumberedChunksAsync(1);

        var logStream = _fixture.Services.GetRequiredService<LogStreamService>();
        await logStream.ProcessChunkAsync(new LogChunk
        {
            JobId = jobId,
            SequenceNumber = 2,
            Content = JobLogSeed.NdjsonLine("o", "still buffered\n"),
            Timestamp = DateTime.UtcNow
        });

        var response = await FeedAsync(jobId, "?after=1");

        Assert.That(await DecodeAsync(response), Is.EqualTo("still buffered\n"));
        Assert.That(SequenceHeader(response, "X-Log-Last-Sequence"), Is.EqualTo(2));
    }

    /// <summary>
    /// A tail read is a viewer establishing the window it will follow from — every later request is
    /// relative to it, so a chunk still sitting in the persistence buffer at that moment is one
    /// nothing would ever go back for.
    /// </summary>
    [Test]
    public async Task Feed_TailRequest_IncludesAChunkStillBufferedForPersistence()
    {
        var jobId = await SeedNumberedChunksAsync(2);

        var logStream = _fixture.Services.GetRequiredService<LogStreamService>();
        await logStream.ProcessChunkAsync(new LogChunk
        {
            JobId = jobId,
            SequenceNumber = 3,
            Content = JobLogSeed.NdjsonLine("o", "still buffered\n"),
            Timestamp = DateTime.UtcNow
        });

        var response = await FeedAsync(jobId, "?limit=200");

        Assert.That(await DecodeAsync(response), Is.EqualTo("line 1\nline 2\nstill buffered\n"));
        Assert.That(SequenceHeader(response, "X-Log-Last-Sequence"), Is.EqualTo(3));
    }

    /// <summary>
    /// A client probing for "anything newer than everything" must not wrap around to the start of
    /// the log.
    /// </summary>
    [Test]
    public async Task Feed_AfterMaxValue_ReturnsNothing()
    {
        var jobId = await SeedNumberedChunksAsync(10);

        var response = await FeedAsync(jobId, $"?after={int.MaxValue}");

        Assert.That(await response.Content.ReadAsStringAsync(), Is.Empty);
    }

    /// <summary>
    /// A page has to stay bounded even when a single write produced a chunk of megabytes, and it has
    /// to keep the end the client is paging *towards* — trimming the other end would leave a hole
    /// between the page and the window the client already holds, which it would never know to
    /// re-fetch.
    /// </summary>
    [Test]
    public async Task Feed_OversizedChunks_TrimThePageOnTheSideAwayFromTheClientsWindow()
    {
        var big = new string('x', 400_000);
        var jobId = await JobLogSeed.CreateJobAsync(_fixture);
        await JobLogSeed.InsertChunksAsync(_fixture, jobId,
            (1, JobLogSeed.NdjsonLine("o", big)),
            (2, JobLogSeed.NdjsonLine("o", big)),
            (3, JobLogSeed.NdjsonLine("o", big)),
            (4, JobLogSeed.NdjsonLine("o", big)));

        var backward = await FeedAsync(jobId, "?limit=200");
        var forward = await FeedAsync(jobId, "?after=0&limit=200");

        // Reading backward from the tail keeps the newest chunks; reading forward keeps the oldest.
        Assert.That(SequenceHeader(backward, "X-Log-Last-Sequence"), Is.EqualTo(4));
        Assert.That(SequenceHeader(backward, "X-Log-First-Sequence"), Is.GreaterThan(1));
        Assert.That(SequenceHeader(forward, "X-Log-First-Sequence"), Is.EqualTo(1));
        Assert.That(SequenceHeader(forward, "X-Log-Last-Sequence"), Is.LessThan(4));

        var backwardBody = await backward.Content.ReadAsStringAsync();
        Assert.That(backwardBody.Length, Is.LessThan(JobsController.MaxPageBytes + big.Length));
    }

    [Test]
    public async Task Feed_DuplicateSequence_IsEmittedOnce()
    {
        var jobId = await JobLogSeed.CreateJobAsync(_fixture);
        await JobLogSeed.InsertChunksAsync(_fixture, jobId, (1, JobLogSeed.NdjsonLine("o", "one\n")));
        await JobLogSeed.InsertChunksAsync(_fixture, jobId, (1, JobLogSeed.NdjsonLine("o", "one\n")));

        var response = await FeedAsync(jobId, "");

        Assert.That(await DecodeAsync(response), Is.EqualTo("one\n"));
    }

    /// <summary>
    /// The viewer's terminal-status set is a string literal in another language, so the exact
    /// spelling on the wire is a contract.
    /// </summary>
    [Test]
    public async Task Feed_JobStatusHeader_MatchesTheJobsReportedStatus()
    {
        var jobId = await SeedNumberedChunksAsync(1);
        var job = await _fixture.GetJobAsync(jobId);

        var response = await FeedAsync(jobId, "");

        Assert.That(response.Headers.GetValues("X-Job-Status").Single(), Is.EqualTo(job.Status.ToString()));
    }

    [Test]
    public async Task Feed_BothBounds_Returns400()
    {
        var jobId = await SeedNumberedChunksAsync(3);

        var response = await FeedAsync(jobId, "?before=3&after=1");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [TestCase("?limit=0")]
    [TestCase("?limit=99999")]
    [TestCase("?limit=-5")]
    public async Task Feed_LimitOutOfRange_Returns400(string query)
    {
        var jobId = await SeedNumberedChunksAsync(3);

        var response = await FeedAsync(jobId, query);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Feed_UnknownJob_Returns404()
    {
        var response = await FeedAsync(Guid.CreateVersion7(), "");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    /// <summary>
    /// The completeness proof: paging backwards to the beginning has to reconstruct exactly what the
    /// download serves, or the viewer is silently dropping part of the log somewhere.
    /// </summary>
    /// <summary>
    /// The completeness proof: paging backwards to the beginning has to reconstruct exactly what the
    /// download serves, or the viewer is silently dropping part of the log somewhere.
    /// </summary>
    [Test]
    public async Task Feed_WalkedBackwardsToTheStart_ReproducesTheDownload()
    {
        var jobId = await SeedNumberedChunksAsync(50);

        var walked = await WalkAsync(jobId, backwards: true);
        var download = await (await _fixture.DownloadJobLogAsync(jobId)).Content.ReadAsStringAsync();

        Assert.That(walked, Is.EqualTo(download));
    }

    /// <summary>
    /// The same completeness proof over chunks big enough to trip the page byte cap, which is where
    /// trimming the wrong end would silently swallow part of the log.
    /// </summary>
    [Test]
    public async Task Feed_WalkedBackwardsOverOversizedChunks_ReproducesTheDownload()
    {
        var jobId = await JobLogSeed.CreateJobAsync(_fixture);
        var chunks = Enumerable.Range(1, 12)
            .Select(i => (i, JobLogSeed.NdjsonLine("o", $"chunk {i} " + new string('x', 300_000) + "\n")))
            .ToArray();
        await JobLogSeed.InsertChunksAsync(_fixture, jobId, chunks);

        var walked = await WalkAsync(jobId, backwards: true);
        var download = await (await _fixture.DownloadJobLogAsync(jobId)).Content.ReadAsStringAsync();

        Assert.That(walked, Is.EqualTo(download));
    }

    /// <summary>
    /// The direction the viewer polls in, which no other test reconstructs end to end.
    /// </summary>
    [Test]
    public async Task Feed_WalkedForwardsToTheEnd_ReproducesTheDownload()
    {
        var jobId = await SeedNumberedChunksAsync(50);

        var walked = await WalkAsync(jobId, backwards: false);
        var download = await (await _fixture.DownloadJobLogAsync(jobId)).Content.ReadAsStringAsync();

        Assert.That(walked, Is.EqualTo(download));
    }

    /// <summary>
    /// log-viewer.js decides when to stop polling by comparing this header against its own list of
    /// terminal statuses, so these spellings are a cross-language contract.
    /// </summary>
    [Test]
    public void JobStatus_TerminalNames_MatchTheViewersList()
    {
        Assert.That(JobStatus.Success.ToString(), Is.EqualTo("Success"));
        Assert.That(JobStatus.Failed.ToString(), Is.EqualTo("Failed"));
        Assert.That(JobStatus.Cancelled.ToString(), Is.EqualTo("Cancelled"));
    }

    /// <summary>
    /// Pages through the whole log in one direction and decodes it, the way the viewer does.
    /// </summary>
    private async Task<string> WalkAsync(Guid jobId, bool backwards)
    {
        var pages = new List<string>();
        var query = backwards ? "?limit=200" : "?after=0&limit=200";

        while (true)
        {
            var response = await FeedAsync(jobId, query);
            var body = await response.Content.ReadAsStringAsync();
            if (body.Length == 0)
            {
                break;
            }

            if (backwards)
            {
                pages.Insert(0, body);
                query = $"?before={SequenceHeader(response, "X-Log-First-Sequence")}&limit=200";
            }
            else
            {
                pages.Add(body);
                query = $"?after={SequenceHeader(response, "X-Log-Last-Sequence")}&limit=200";
            }
        }

        return string.Concat(string.Concat(pages)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(line => LogChunkParser.ParseEntries(line))
            .Select(entry => entry.Data));
    }

    private async Task<Guid> SeedNumberedChunksAsync(int count)
    {
        var jobId = await JobLogSeed.CreateJobAsync(_fixture);
        var chunks = Enumerable.Range(1, count)
            .Select(i => (i, JobLogSeed.NdjsonLine("o", $"line {i}\n")))
            .ToArray();
        await JobLogSeed.InsertChunksAsync(_fixture, jobId, chunks);
        return jobId;
    }

    private async Task<HttpResponseMessage> FeedAsync(Guid jobId, string query)
    {
        return await _fixture.HttpClient.GetAsync($"/api/jobs/{jobId}/logs/chunks{query}");
    }

    /// <summary>
    /// Decodes a feed response the way the viewer does, so assertions read as log text.
    /// </summary>
    private static async Task<string> DecodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var text = new StringBuilder();

        foreach (var entry in LogChunkParser.ParseEntries(body))
        {
            text.Append(entry.Data);
        }

        return text.ToString();
    }

    private static int SequenceHeader(HttpResponseMessage response, string name)
    {
        return int.Parse(response.Headers.GetValues(name).Single());
    }
}
