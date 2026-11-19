using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Ilmarinen.Worker.Services;
using NUnit.Framework;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Core.Tests;

[TestFixture]
public class ResultOutboxTests
{
    [Test]
    public async Task ReplayAsync_SendsOldestFirst_AndEmptiesTheOutbox()
    {
        var outbox = new ResultOutbox();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        outbox.Add(first, Result(first));
        outbox.Add(second, Result(second));
        var sent = new List<Guid>();

        await outbox.ReplayAsync(entry =>
        {
            sent.Add(entry.JobId);
            return Task.CompletedTask;
        });

        Assert.That(sent, Is.EqualTo(new[] { first, second }));
        Assert.That(outbox.IsEmpty, Is.True);
    }

    [Test]
    public void ReplayAsync_WhenASendFails_KeepsItAndEverythingBehindIt()
    {
        var outbox = new ResultOutbox();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        outbox.Add(first, Result(first));
        outbox.Add(second, Result(second));

        Assert.ThrowsAsync<InvalidOperationException>(() => outbox.ReplayAsync(_ => throw new InvalidOperationException("connection lost")));

        var remaining = new List<Guid>();
        Assert.DoesNotThrowAsync(() => outbox.ReplayAsync(entry =>
        {
            remaining.Add(entry.JobId);
            return Task.CompletedTask;
        }));
        Assert.That(remaining, Is.EqualTo(new[] { first, second }));
    }

    // The MessageBuffer gives up on a message after a few failed replays so the rest can get through; a result must not be given up on, however often its send fails.
    [Test]
    public void ReplayAsync_AResultThatKeepsFailing_IsNeverDropped()
    {
        var outbox = new ResultOutbox();
        var jobId = Guid.NewGuid();
        outbox.Add(jobId, Result(jobId));

        for (var i = 0; i < 20; i++)
        {
            Assert.ThrowsAsync<InvalidOperationException>(() => outbox.ReplayAsync(_ => throw new InvalidOperationException("refused")));
        }

        Assert.That(outbox.IsEmpty, Is.False);
    }

    [Test]
    public void Acknowledge_RemovesOnlyThatEntry_EvenWhenAnotherNamesTheSameJob()
    {
        var outbox = new ResultOutbox();
        var jobId = Guid.NewGuid();
        var rejection = outbox.Add(jobId, Result(jobId, JobStatus.Failed));
        outbox.Add(jobId, Result(jobId, JobStatus.Success));

        outbox.Acknowledge(rejection);

        var remaining = new List<JobStatus>();
        Assert.DoesNotThrowAsync(() => outbox.ReplayAsync(entry =>
        {
            remaining.Add(entry.Result.Status);
            return Task.CompletedTask;
        }));
        Assert.That(remaining, Is.EqualTo(new[] { JobStatus.Success }));
    }

    private static JobResult Result(Guid jobId, JobStatus status = JobStatus.Success)
    {
        return new JobResult { Id = jobId, Status = status, Duration = TimeSpan.FromSeconds(1) };
    }
}
