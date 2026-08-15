using Microsoft.Extensions.Logging;
using System;

namespace Ilmarinen.Worker.Services;

/// <summary>
/// Forwards log calls to the worker's own logger and mirrors each formatted line into the job's LogCollector, so worker-side activity during a job shows up in the job output. Warning and above map to stderr ("e"); everything else is metadata ("m"). Every call is mirrored regardless of level — callers are all Information+, and job output deliberately ignores the worker's console verbosity.
/// </summary>
public class LoggerTee : ILogger
{
    private readonly ILogger _inner;
    private readonly LogCollector _collector;

    public LoggerTee(ILogger inner, LogCollector collector)
    {
        _inner = inner;
        _collector = collector;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return _inner.BeginScope(state);
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        if (logLevel == LogLevel.None)
        {
            return false;
        }

        // Job output must always capture Information and above, no matter how quiet the worker's own console logging is configured.
        return logLevel >= LogLevel.Information || _inner.IsEnabled(logLevel);
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        _inner.Log(logLevel, eventId, state, exception, formatter);

        var message = formatter(state, exception);
        if (exception != null)
        {
            // Full ToString so the job log carries the exception type and stack trace, not just the message.
            message += "\n" + exception;
        }

        _collector.Write(logLevel >= LogLevel.Warning ? "e" : "m", message + "\n");
    }
}
