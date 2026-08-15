using Ilmarinen.Database;
using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// Covers GET /api/jobs/{id}/logs/stream, the Server-Sent Events tail the viewer follows a running
/// job with. Its whole reason to exist is arriving sooner than the chunks feed can, so these tests
/// pin that: a chunk reaches a connected client while it is still in the persistence buffer.
/// </summary>
[TestFixture]
[Category("Integration")]
public class JobLogStreamTests
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
    public async Task Stream_ChunkReceivedWhileConnected_ArrivesBeforeItIsPersisted()
    {
        var jobId = await JobLogSeed.CreateJobAsync(_fixture);

        using var stream = await OpenAsync(jobId);
        Assert.That(stream.Response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/event-stream"));

        await PushChunkAsync(jobId, 1, "hello from the worker\n");
        var received = await stream.ReadRequiredEventAsync();

        Assert.That(received.Name, Is.EqualTo("chunk"));
        Assert.That(received.Id, Is.EqualTo("1"));
        Assert.That(received.Data, Is.EqualTo(JobLogSeed.NdjsonLine("o", "hello from the worker\n").TrimEnd('\n')));
        Assert.That(await PersistedChunkCountAsync(jobId), Is.Zero, "the live tail exists to beat persistence, not to follow it");
    }

    /// <summary>
    /// The client tells contiguous chunks from gaps by sequence, so the id has to be the chunk's own
    /// sequence number and not a count of what this connection has sent.
    /// </summary>
    [Test]
    public async Task Stream_SuccessiveChunks_CarryTheirOwnSequenceNumbers()
    {
        var jobId = await JobLogSeed.CreateJobAsync(_fixture);

        using var stream = await OpenAsync(jobId);
        await PushChunkAsync(jobId, 7, "seven\n");
        await PushChunkAsync(jobId, 8, "eight\n");

        Assert.That((await stream.ReadRequiredEventAsync()).Id, Is.EqualTo("7"));
        Assert.That((await stream.ReadRequiredEventAsync()).Id, Is.EqualTo("8"));
    }

    /// <summary>
    /// A chunk holds a run of NDJSON lines, and the viewer parses the reassembled body — so every
    /// line has to survive the trip as its own data field.
    /// </summary>
    [Test]
    public async Task Stream_MultiLineChunk_ArrivesWithEveryLine()
    {
        var jobId = await JobLogSeed.CreateJobAsync(_fixture);
        var content = JobLogSeed.NdjsonLine("o", "first\n") + JobLogSeed.NdjsonLine("e", "second\n");

        using var stream = await OpenAsync(jobId);
        await PushRawChunkAsync(jobId, 1, content);
        var received = await stream.ReadRequiredEventAsync();

        Assert.That(received.Data, Is.EqualTo(content.TrimEnd('\n')));
    }

    [Test]
    public async Task Stream_JobCompletes_EmitsStatusThenEndsTheStream()
    {
        var jobId = await JobLogSeed.CreateJobAsync(_fixture);

        using var stream = await OpenAsync(jobId);
        var logStream = _fixture.Services.GetRequiredService<LogStreamService>();
        await logStream.NotifyJobCompletedAsync(jobId, JobStatus.Success);

        var received = await stream.ReadRequiredEventAsync();

        Assert.That(received.Name, Is.EqualTo("status"));
        Assert.That(received.Data, Is.EqualTo("Success"));
        Assert.That(await stream.ReadEventAsync(), Is.Null, "a finished job has nothing more to stream");
    }

    /// <summary>
    /// The viewer opens the stream from whatever status the page was rendered with, so a job that
    /// finished in between must be told rather than left holding an open connection forever.
    /// </summary>
    [Test]
    public async Task Stream_JobAlreadyTerminal_EmitsStatusAndCloses()
    {
        var jobId = await JobLogSeed.CreateJobAsync(_fixture);
        using (var scope = _fixture.Services.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
            await jobs.UpdateStatusAsync(jobId, JobStatus.Failed);
        }

        using var stream = await OpenAsync(jobId);
        var received = await stream.ReadRequiredEventAsync();

        Assert.That(received.Name, Is.EqualTo("status"));
        Assert.That(received.Data, Is.EqualTo("Failed"));
        Assert.That(await stream.ReadEventAsync(), Is.Null);
    }

    /// <summary>
    /// The client's whole gap protocol rests on the stream never replaying: it appends a pushed chunk
    /// only when the sequence is the next one it needs, and re-reads the feed for anything else. A
    /// stream that helpfully sent history would have every client refetching on connect.
    /// </summary>
    [Test]
    public async Task Stream_JobWithStoredLog_SendsNothingUntilSomethingNewArrives()
    {
        var jobId = await JobLogSeed.CreateJobAsync(_fixture);
        await JobLogSeed.InsertChunksAsync(_fixture, jobId,
            (1, JobLogSeed.NdjsonLine("o", "old\n")),
            (2, JobLogSeed.NdjsonLine("o", "older\n")));

        using var stream = await OpenAsync(jobId);
        await PushChunkAsync(jobId, 3, "new\n");
        var received = await stream.ReadRequiredEventAsync();

        Assert.That(received.Id, Is.EqualTo("3"), "the stream carries the live tail, never history");
    }

    /// <summary>
    /// A chunk is whatever one worker-side flush produced, so it can be arbitrarily large. Holding a
    /// run of those per connected client is how a log page turns into a memory incident.
    /// </summary>
    [Test]
    public async Task Stream_OversizedChunk_AnnouncesTheSequenceWithoutTheContent()
    {
        var jobId = await JobLogSeed.CreateJobAsync(_fixture);

        using var stream = await OpenAsync(jobId);
        await PushRawChunkAsync(jobId, 1, JobLogSeed.NdjsonLine("o", new string('x', 200_000)));
        var received = await stream.ReadRequiredEventAsync();

        Assert.That(received.Id, Is.EqualTo("1"));
        Assert.That(received.Data, Is.Empty, "the client is expected to fetch this one from the feed");
    }

    [Test]
    public async Task Stream_TwoViewersOnOneJob_BothReceiveTheChunk()
    {
        var jobId = await JobLogSeed.CreateJobAsync(_fixture);

        using var first = await OpenAsync(jobId);
        using var second = await OpenAsync(jobId);
        await PushChunkAsync(jobId, 1, "watched twice\n");

        Assert.That((await first.ReadRequiredEventAsync()).Id, Is.EqualTo("1"));
        Assert.That((await second.ReadRequiredEventAsync()).Id, Is.EqualTo("1"));
    }

    /// <summary>
    /// Every closed page has to take its subscription with it, or the fan-out list grows for the life
    /// of the process and every chunk pays for readers that left.
    /// </summary>
    [Test]
    public async Task Stream_ClientDisconnects_DropsItsSubscription()
    {
        var jobId = await JobLogSeed.CreateJobAsync(_fixture);
        var subscriptions = _fixture.Services.GetRequiredService<LogSubscriptionService>();

        var stream = await OpenAsync(jobId);
        await PushChunkAsync(jobId, 1, "watching\n");
        await stream.ReadRequiredEventAsync();
        Assert.That(subscriptions.SubscriberCount(jobId), Is.EqualTo(1));

        stream.Dispose();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (subscriptions.SubscriberCount(jobId) > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.That(subscriptions.SubscriberCount(jobId), Is.Zero);
    }

    [Test]
    public async Task Stream_UnknownJob_Returns404()
    {
        var response = await _fixture.HttpClient.GetAsync($"/api/jobs/{Guid.CreateVersion7()}/logs/stream", HttpCompletionOption.ResponseHeadersRead);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    private async Task<int> PersistedChunkCountAsync(Guid jobId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        return await db.JobLogChunks.CountAsync(c => c.JobId == jobId);
    }

    private Task PushChunkAsync(Guid jobId, int sequence, string line)
    {
        return PushRawChunkAsync(jobId, sequence, JobLogSeed.NdjsonLine("o", line));
    }

    /// <summary>Hands a chunk to the server exactly as the worker hub does.</summary>
    private async Task PushRawChunkAsync(Guid jobId, int sequence, string content)
    {
        var logStream = _fixture.Services.GetRequiredService<LogStreamService>();
        await logStream.ProcessChunkAsync(new LogChunk
        {
            JobId = jobId,
            SequenceNumber = sequence,
            Content = content,
            Timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Returns once the response headers are in, which is also the point at which the server has
    /// subscribed — so anything pushed after this call is guaranteed to be seen.
    /// </summary>
    private async Task<SseStream> OpenAsync(Guid jobId)
    {
        var response = await _fixture.HttpClient.GetAsync(
            $"/api/jobs/{jobId}/logs/stream",
            HttpCompletionOption.ResponseHeadersRead);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return new SseStream(response, await response.Content.ReadAsStreamAsync());
    }

    private record SseEvent(string? Id, string Name, string Data);

    /// <summary>Reads SSE frames off a live response, one blank-line-terminated event at a time.</summary>
    private sealed class SseStream : IDisposable
    {
        private readonly StreamReader _reader;

        public SseStream(HttpResponseMessage response, Stream body)
        {
            Response = response;
            _reader = new StreamReader(body);
        }

        public HttpResponseMessage Response { get; }

        /// <summary>The next event, failing the test rather than throwing a null reference if the stream ended instead.</summary>
        public async Task<SseEvent> ReadRequiredEventAsync()
        {
            var received = await ReadEventAsync();
            Assert.That(received, Is.Not.Null, "the stream ended without sending the expected event");
            return received!;
        }

        /// <summary>
        /// The next event, or null once the server closes the stream. Dispatches on the same rule a
        /// browser uses — a frame whose data buffer is empty sets the last event id and is otherwise
        /// discarded — because a parser more generous than EventSource would pass frames no real
        /// client ever sees.
        /// </summary>
        public async Task<SseEvent?> ReadEventAsync(int timeoutMs = 15000)
        {
            using var timeout = new CancellationTokenSource(timeoutMs);
            string? id = null;
            var name = "message";
            var data = new List<string>();

            while (true)
            {
                var line = await _reader.ReadLineAsync(timeout.Token);

                if (line == null)
                {
                    return null;
                }

                if (line.Length == 0)
                {
                    if (data.Count > 0)
                    {
                        return new SseEvent(id, name, string.Join('\n', data));
                    }

                    name = "message";
                    continue; // A keepalive comment, or a frame carrying nothing but an id.
                }

                if (line.StartsWith(':'))
                {
                    continue;
                }

                var (field, value) = Split(line);
                switch (field)
                {
                    case "id":
                        id = value;
                        break;
                    case "event":
                        name = value;
                        break;
                    case "data":
                        data.Add(value);
                        break;
                }
            }
        }

        private static (string Field, string Value) Split(string line)
        {
            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                return (line, "");
            }

            var value = line[(colon + 1)..];
            return (line[..colon], value.StartsWith(' ') ? value[1..] : value);
        }

        public void Dispose()
        {
            _reader.Dispose();
            Response.Dispose();
        }
    }
}
