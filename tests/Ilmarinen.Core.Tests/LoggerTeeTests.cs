using Ilmarinen.Worker.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Core.Tests;

[TestFixture]
public class LoggerTeeTests
{
    private HubConnection _connection = null!;
    private MessageBuffer _messageBuffer = null!;
    private LoggerCapture _inner = null!;
    private LogCollector _collector = null!;
    private LoggerTee _tee = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = new HubConnectionBuilder().WithUrl("http://localhost:1").Build();
        _messageBuffer = new MessageBuffer();
        _inner = new LoggerCapture();
        _collector = new LogCollector(Guid.NewGuid(), _connection, _messageBuffer, _inner);
        _tee = new LoggerTee(_inner, _collector);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _connection.DisposeAsync();
    }

    private async Task<List<(string Type, string Data)>> FlushAndDrainAsync()
    {
        await _collector.FlushAsync();
        return LogChunkDrain.DrainEntries(_messageBuffer);
    }

    [Test]
    public async Task Information_MapsToMetadata()
    {
        _tee.LogInformation("cloning {Url}", "http://example.invalid");

        var entries = await FlushAndDrainAsync();

        Assert.That(entries, Does.Contain(("m", "cloning http://example.invalid\n")));
    }

    [Test]
    public async Task Warning_MapsToStderr()
    {
        _tee.LogWarning("something odd");

        var entries = await FlushAndDrainAsync();

        Assert.That(entries, Does.Contain(("e", "something odd\n")));
    }

    [Test]
    public async Task Error_WithException_AppendsTypeAndStack()
    {
        Exception thrown;
        try
        {
            throw new InvalidOperationException("kaboom");
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        _tee.LogError(thrown, "job failed");

        var entries = await FlushAndDrainAsync();

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].Type, Is.EqualTo("e"));
        Assert.That(entries[0].Data, Does.StartWith("job failed\n"));
        Assert.That(entries[0].Data, Does.Contain("InvalidOperationException"));
        Assert.That(entries[0].Data, Does.Contain("kaboom"));
        Assert.That(entries[0].Data, Does.Contain(nameof(Error_WithException_AppendsTypeAndStack)), "stack trace should be included");
    }

    [Test]
    public void Log_ForwardsToInnerLogger()
    {
        _tee.LogInformation("forwarded");

        Assert.That(_inner.Entries, Has.Some.Matches<LoggerCapture.Entry>(e => e.Level == LogLevel.Information && e.Message == "forwarded"));
    }

    [Test]
    public void IsEnabled_InformationAndAbove_TrueEvenWhenInnerDisabled()
    {
        _inner.EnabledResult = false;

        Assert.That(_tee.IsEnabled(LogLevel.Information), Is.True);
        Assert.That(_tee.IsEnabled(LogLevel.Error), Is.True);
        Assert.That(_tee.IsEnabled(LogLevel.Debug), Is.False);
        Assert.That(_tee.IsEnabled(LogLevel.None), Is.False);
    }
}
