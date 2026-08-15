using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System;

namespace Ilmarinen.Core.Tests;

/// <summary>
/// Test ILogger that records every call so assertions can inspect level, message, and exception.
/// </summary>
public class LoggerCapture : ILogger
{
    public record Entry(LogLevel Level, string Message, Exception? Exception);

    public List<Entry> Entries { get; } = new();

    public bool EnabledResult { get; set; } = true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return EnabledResult;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Add(new Entry(logLevel, formatter(state, exception), exception));
    }
}
