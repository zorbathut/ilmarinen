using Ilmarinen.Worker.Services;
using NUnit.Framework;
using System.Linq;
using System;

namespace Ilmarinen.Core.Tests;

[TestFixture]
public class MessageBufferTests
{
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
        Assert.That(oldest!.Method, Is.EqualTo("chunk-1"), "the oldest goes first, so the newest — a job's result — survives");
    }

    private static BufferedMessage Message(string method)
    {
        return new BufferedMessage { Method = method, Args = [] };
    }
}
