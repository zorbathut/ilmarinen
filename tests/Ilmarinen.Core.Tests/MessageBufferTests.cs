using Ilmarinen.Worker.Services;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Core.Tests;

[TestFixture]
public class MessageBufferTests
{
    [Test]
    public async Task ReplayAsync_DeliversInTheOrderTheyWereBuffered()
    {
        var buffer = new MessageBuffer();
        buffer.Enqueue(Message("first"));
        buffer.Enqueue(Message("second"));

        var sent = new List<string>();
        await buffer.ReplayAsync(m => { sent.Add(m.Method); return Task.FromResult(true); }, new LoggerCapture());

        Assert.That(sent, Is.EqualTo(new[] { "first", "second" }));
        Assert.That(buffer.IsEmpty, Is.True);
    }

    [Test]
    public async Task ReplayAsync_WhenASendFails_KeepsItAndEverythingBehindIt()
    {
        var buffer = new MessageBuffer();
        buffer.Enqueue(Message("first"));
        buffer.Enqueue(Message("second"));

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await buffer.ReplayAsync(_ => throw new InvalidOperationException("server is down"), new LoggerCapture()));

        var sent = new List<string>();
        await buffer.ReplayAsync(m => { sent.Add(m.Method); return Task.FromResult(true); }, new LoggerCapture());
        Assert.That(sent, Is.EqualTo(new[] { "first", "second" }), "a failed replay must not lose the message or reorder the queue");
    }

    // Without a bound, one message the server will never accept blocks every message behind it — and with them every
    // sync, leaving a worker that never resumes and never drains.
    [Test]
    public async Task ReplayAsync_AMessageThatKeepsFailing_IsDroppedSoTheRestGetThrough()
    {
        var buffer = new MessageBuffer();
        buffer.Enqueue(Message("poison"));
        buffer.Enqueue(Message("result"));

        var logger = new LoggerCapture();
        var delivered = new List<string>();

        for (var attempt = 0; attempt < 20 && !buffer.IsEmpty; attempt++)
        {
            try
            {
                await buffer.ReplayAsync(m =>
                {
                    if (m.Method == "poison")
                    {
                        throw new InvalidOperationException("the server refuses this one");
                    }
                    delivered.Add(m.Method);
                    return Task.FromResult(true);
                }, logger);
            }
            catch (InvalidOperationException)
            {
                // Each sync gives up on the first failure and retries on the next reconnect.
            }
        }

        Assert.That(delivered, Is.EqualTo(new[] { "result" }));
        Assert.That(buffer.IsEmpty, Is.True);
        Assert.That(logger.Entries.Any(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error), Is.True, "dropping a message is a loss and has to be reported");
    }

    [Test]
    public void Enqueue_PastTheCap_DropsTheOldestAndSaysSo()
    {
        var buffer = new MessageBuffer();

        for (var i = 0; i < MessageBuffer.MaxMessages; i++)
        {
            Assert.That(buffer.Enqueue(Message($"chunk-{i}")), Is.True);
        }

        Assert.That(buffer.Enqueue(Message("result")), Is.False, "the caller has to be able to report that output was lost");

        buffer.TryDequeue(out var oldest);
        Assert.That(oldest!.Method, Is.EqualTo("chunk-1"), "the oldest goes first, so the newest survives");
    }

    // A connection the server has superseded refuses everything; that says nothing about the message, which must not be given up on for it.
    [Test]
    public async Task ReplayAsync_RefusedByTheConnection_StopsWithoutCountingAgainstTheMessage()
    {
        var buffer = new MessageBuffer();
        buffer.Enqueue(Message("chunk"));

        for (var attempt = 0; attempt < 20; attempt++)
        {
            Assert.That(await buffer.ReplayAsync(_ => Task.FromResult(false), new LoggerCapture()), Is.False);
        }

        var sent = new List<string>();
        Assert.That(await buffer.ReplayAsync(m => { sent.Add(m.Method); return Task.FromResult(true); }, new LoggerCapture()), Is.True);
        Assert.That(sent, Is.EqualTo(new[] { "chunk" }));
    }

    private static BufferedMessage Message(string method)
    {
        return new BufferedMessage { Method = method, Args = [] };
    }
}
