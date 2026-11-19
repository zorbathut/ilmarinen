using Ilmarinen.Cli;
using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// Covers the CLI's job follower against a real server: every chunk the job wrote reaches the
/// reader exactly once and in order, and the follow ends with the job's final status.
/// </summary>
[TestFixture]
[Category("Integration")]
public class JobLogFollowerTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(1);

    private IntegrationTestFixture _fixture = null!;
    private LogStreamService _logStream = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new IntegrationTestFixture();
        await _fixture.SetupAsync();
        _logStream = _fixture.Services.GetRequiredService<LogStreamService>();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _fixture.DisposeAsync();
    }

    [Test]
    public async Task Follow_FinishedJob_EmitsItsWholeLogAndReturnsItsStatus()
    {
        var jobId = await JobLogSeed.CreateJobAsync(_fixture);
        await JobLogSeed.InsertChunksAsync(_fixture, jobId,
            (1, JobLogSeed.NdjsonLine("m", "step: build\n")),
            (2, JobLogSeed.NdjsonLine("o", "compiling\n") + JobLogSeed.NdjsonLine("e", "a warning\n")),
            (3, JobLogSeed.NdjsonLine("o", "done\n")));
        await CompleteAsync(jobId, JobStatus.Success);

        var follow = new Follow(_fixture, jobId);
        var status = await follow.RunAsync();

        Assert.That(status, Is.EqualTo(JobStatus.Success));
        Assert.That(follow.Entries, Is.EqualTo(new[]
        {
            ("m", "step: build\n"),
            ("o", "compiling\n"),
            ("e", "a warning\n"),
            ("o", "done\n")
        }));
    }

    /// <summary>
    /// Output the server has received but not yet persisted is exactly what a follower that starts
    /// mid-job most needs, since it is the newest.
    /// </summary>
    [Test]
    public async Task Follow_ChunksStillBufferedWhenFollowStarts_AreEmitted()
    {
        var jobId = await JobLogSeed.CreateJobAsync(_fixture);
        await PushAsync(jobId, 1, "first\n");
        await PushAsync(jobId, 2, "second\n");

        var follow = new Follow(_fixture, jobId);
        var running = follow.RunAsync();

        await follow.WaitForEntriesAsync(2);
        await CompleteAsync(jobId, JobStatus.Success);

        Assert.That(await running, Is.EqualTo(JobStatus.Success));
        Assert.That(follow.Texts, Is.EqualTo(new[] { "first\n", "second\n" }));
    }

    [Test]
    public async Task Follow_ChunksArrivingWhileFollowing_AreEmittedInOrder()
    {
        var jobId = await JobLogSeed.CreateJobAsync(_fixture);

        var follow = new Follow(_fixture, jobId);
        var running = follow.RunAsync();

        await PushAsync(jobId, 1, "one\n");
        await follow.WaitForEntriesAsync(1);
        await PushAsync(jobId, 2, "two\n");
        await PushAsync(jobId, 3, "three\n");
        await follow.WaitForEntriesAsync(3);
        await CompleteAsync(jobId, JobStatus.Failed);

        Assert.That(await running, Is.EqualTo(JobStatus.Failed));
        Assert.That(follow.Texts, Is.EqualTo(new[] { "one\n", "two\n", "three\n" }));
    }

    /// <summary>
    /// The job's last chunks are its summary, and they reach the server in the moments before it reports completion, so a read can find the job finished with that output still to come — the follow must keep reading until a page comes back empty, not stop at the first terminal status.
    /// </summary>
    [Test]
    public async Task Follow_ChunksBufferedRightBeforeCompletion_AreAllEmitted()
    {
        var jobId = await JobLogSeed.CreateJobAsync(_fixture);

        var follow = new Follow(_fixture, jobId);
        var running = follow.RunAsync();
        await follow.WaitForStatusAsync(JobStatus.Queued);

        await PushAsync(jobId, 1, "tests passed\n");
        await CompleteAsync(jobId, JobStatus.Success);

        Assert.That(await running, Is.EqualTo(JobStatus.Success));
        Assert.That(follow.Texts, Is.EqualTo(new[] { "tests passed\n" }));
    }

    /// <summary>One worker flush can make a single enormous chunk, and the feed has to carry it whole.</summary>
    [Test]
    public async Task Follow_ChunkOver64Kb_IsEmittedInFull()
    {
        var jobId = await JobLogSeed.CreateJobAsync(_fixture);
        var big = new string('x', 100 * 1024) + "\n";

        var follow = new Follow(_fixture, jobId);
        var running = follow.RunAsync();

        await PushAsync(jobId, 1, big);
        await follow.WaitForEntriesAsync(1);
        await CompleteAsync(jobId, JobStatus.Success);

        Assert.That(await running, Is.EqualTo(JobStatus.Success));
        Assert.That(follow.Texts, Is.EqualTo(new[] { big }));
    }

    /// <summary>A worker re-sends a chunk whose send only appeared to fail, so the same sequence can be stored twice.</summary>
    [Test]
    public async Task Follow_SequenceStoredTwice_IsEmittedOnce()
    {
        var jobId = await JobLogSeed.CreateJobAsync(_fixture);
        await JobLogSeed.InsertChunksAsync(_fixture, jobId,
            (1, JobLogSeed.NdjsonLine("o", "one\n")),
            (2, JobLogSeed.NdjsonLine("o", "two\n")),
            (2, JobLogSeed.NdjsonLine("o", "two\n")),
            (3, JobLogSeed.NdjsonLine("o", "three\n")));
        await CompleteAsync(jobId, JobStatus.Success);

        var follow = new Follow(_fixture, jobId);
        await follow.RunAsync();

        Assert.That(follow.Texts, Is.EqualTo(new[] { "one\n", "two\n", "three\n" }));
    }

    /// <summary>More chunks than one page holds, and more bytes than one page may carry, so both page limits are crossed.</summary>
    [Test]
    public async Task Follow_LogLargerThanOnePage_IsEmittedCompletely()
    {
        var jobId = await JobLogSeed.CreateJobAsync(_fixture);
        var padding = new string('p', 6000);
        var chunks = Enumerable.Range(1, 450)
            .Select(i => (i, JobLogSeed.NdjsonLine("o", $"{i} {padding}\n")))
            .ToArray();
        await JobLogSeed.InsertChunksAsync(_fixture, jobId, chunks);
        await CompleteAsync(jobId, JobStatus.Success);

        var follow = new Follow(_fixture, jobId);
        await follow.RunAsync();

        Assert.That(follow.Texts, Is.EqualTo(Enumerable.Range(1, 450).Select(i => $"{i} {padding}\n")));
    }

    /// <summary>A job can wait a long time for a worker without writing anything, and the reader should be able to tell that wait apart from a hang.</summary>
    [Test]
    public async Task Follow_StatusChangesWithoutOutput_AreReported()
    {
        var jobId = await JobLogSeed.CreateJobAsync(_fixture);

        var follow = new Follow(_fixture, jobId);
        var running = follow.RunAsync();

        await follow.WaitForStatusAsync(JobStatus.Queued);
        using (var scope = _fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<JobRepository>().AssignAsync(jobId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        }

        await follow.WaitForStatusAsync(JobStatus.Running);
        await CompleteAsync(jobId, JobStatus.Success);

        Assert.That(await running, Is.EqualTo(JobStatus.Success));
        Assert.That(follow.Statuses, Is.EqualTo(new[] { JobStatus.Queued, JobStatus.Running, JobStatus.Success }));
        Assert.That(follow.Entries, Is.Empty);
    }

    [Test]
    public void Follow_UnknownJob_ThrowsJobNotFound()
    {
        var follow = new Follow(_fixture, Guid.NewGuid());

        Assert.ThrowsAsync<JobNotFoundException>(async () => await follow.RunAsync());
    }

    [Test]
    public async Task Follow_Cancelled_StopsPromptly()
    {
        var jobId = await JobLogSeed.CreateJobAsync(_fixture);
        using var cts = new CancellationTokenSource();

        var follow = new Follow(_fixture, jobId);
        var running = follow.RunAsync(cts.Token);
        await follow.WaitForStatusAsync(JobStatus.Queued);

        cts.Cancel();

        var finished = await Task.WhenAny(running, Task.Delay(WaitTimeout));
        Assert.That(finished, Is.SameAs(running), "the follow should stop as soon as it is cancelled");
        Assert.That(running.IsCanceled || running.Exception?.InnerException is OperationCanceledException, Is.True);
    }

    private async Task PushAsync(Guid jobId, int sequence, string text)
    {
        await _logStream.ProcessChunkAsync(new LogChunk
        {
            JobId = jobId,
            SequenceNumber = sequence,
            Content = JobLogSeed.NdjsonLine("o", text),
            Timestamp = DateTime.UtcNow
        });
    }

    private async Task CompleteAsync(Guid jobId, JobStatus status)
    {
        using var scope = _fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<JobScheduler>().CompleteJobAsync(jobId, status);
    }

    /// <summary>One follower run, recording everything it reports.</summary>
    private sealed class Follow
    {
        private readonly IntegrationTestFixture _fixture;
        private readonly Guid _jobId;
        private readonly object _lock = new();
        private readonly List<(string Type, string Data)> _entries = [];
        private readonly List<JobStatus> _statuses = [];

        public Follow(IntegrationTestFixture fixture, Guid jobId)
        {
            _fixture = fixture;
            _jobId = jobId;
        }

        public IReadOnlyList<(string Type, string Data)> Entries
        {
            get
            {
                lock (_lock)
                {
                    return _entries.ToList();
                }
            }
        }

        public IReadOnlyList<string> Texts
        {
            get
            {
                return Entries.Select(e => e.Data).ToList();
            }
        }

        public IReadOnlyList<JobStatus> Statuses
        {
            get
            {
                lock (_lock)
                {
                    return _statuses.ToList();
                }
            }
        }

        public Task<JobStatus> RunAsync(CancellationToken cancellationToken = default)
        {
            var follower = new JobLogFollower(
                _fixture.HttpClient,
                _jobId,
                PollInterval,
                GiveUpAfter,
                entry =>
                {
                    lock (_lock)
                    {
                        _entries.Add((entry.Type, entry.Data));
                    }
                },
                status =>
                {
                    lock (_lock)
                    {
                        _statuses.Add(status);
                    }
                },
                warning => Assert.Fail($"unexpected follower warning: {warning}"));

            return follower.FollowAsync(cancellationToken);
        }

        public async Task WaitForEntriesAsync(int count)
        {
            await WaitUntilAsync(() => Entries.Count >= count, $"{count} entries");
        }

        public async Task WaitForStatusAsync(JobStatus status)
        {
            await WaitUntilAsync(() => Statuses.Contains(status), $"status {status}");
        }

        private static async Task WaitUntilAsync(Func<bool> condition, string what)
        {
            var deadline = DateTime.UtcNow + WaitTimeout;
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail($"timed out waiting for {what}");
                }

                await Task.Delay(20);
            }
        }
    }
}
