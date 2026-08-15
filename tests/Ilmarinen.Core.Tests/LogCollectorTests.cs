using Ilmarinen.Worker.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Core.Tests;

[TestFixture]
public class LogCollectorTests
{
    private HubConnection _connection = null!;
    private MessageBuffer _messageBuffer = null!;
    private LoggerCapture _logger = null!;
    private LogCollector _collector = null!;

    [SetUp]
    public void SetUp()
    {
        // Never started, so the connection stays Disconnected and every chunk lands in the inspectable MessageBuffer instead of going over the wire.
        _connection = new HubConnectionBuilder().WithUrl("http://localhost:1").Build();
        _messageBuffer = new MessageBuffer();
        _logger = new LoggerCapture();
        _collector = new LogCollector(Guid.NewGuid(), _connection, _messageBuffer, _logger);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _connection.DisposeAsync();
    }

    [Test]
    public async Task WritesBeforeFlush_AreDeliveredAsChunks()
    {
        _collector.Write("o", "hello stdout\n");
        _collector.Write("e", "hello stderr\n");
        await _collector.FlushAsync();

        var entries = LogChunkDrain.DrainEntries(_messageBuffer);

        Assert.That(entries, Does.Contain(("o", "hello stdout\n")));
        Assert.That(entries, Does.Contain(("e", "hello stderr\n")));
    }

    [Test]
    public async Task WriteAfterFlush_IsDroppedWithWarning()
    {
        _collector.Write("o", "before flush\n");
        await _collector.FlushAsync();
        LogChunkDrain.DrainEntries(_messageBuffer);

        _collector.Write("o", "after flush\n");

        Assert.That(LogChunkDrain.DrainEntries(_messageBuffer), Is.Empty);
        Assert.That(_logger.Entries, Has.Some.Matches<LoggerCapture.Entry>(e => e.Level == LogLevel.Warning && e.Message.Contains("after flush")));
    }
}
