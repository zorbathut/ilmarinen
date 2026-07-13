using Ilmarinen.Worker.Services;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Core.Tests;

[TestFixture]
public class SleepInhibitorTests
{
    private const string UnreachableBus = "unix:path=/nonexistent/ilmarinen-no-such-bus";

    private ListLogger<SleepInhibitor> _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _logger = new ListLogger<SleepInhibitor>();
    }

    [Test]
    public async Task AcquireAsync_BusUnreachable_DoesNotThrowAndWarnsImmediately()
    {
        var inhibitor = new SleepInhibitor(_logger, UnreachableBus);

        using var handle = await inhibitor.AcquireAsync("job-1");

        Assert.That(handle, Is.Not.Null);

        // The warning must land at acquire time. If it only landed at release, a host that suspends mid-job would freeze the worker and the warning would never be printed at all — which is precisely the case this feature exists to cover.
        var warnings = _logger.Records.Where(r => r.Level == LogLevel.Warning).ToList();
        Assert.That(warnings, Has.Count.EqualTo(1));
        Assert.That(warnings[0].Message, Does.Contain("system_bus_socket"), "the warning should tell the user how to fix it");
    }

    [Test]
    public async Task AcquireAsync_BusUnreachable_WarnsOnceThenLogsQuietly()
    {
        var inhibitor = new SleepInhibitor(_logger, UnreachableBus);

        using (await inhibitor.AcquireAsync("job-1")) { }
        using (await inhibitor.AcquireAsync("job-2")) { }
        using (await inhibitor.AcquireAsync("job-3")) { }

        // A worker on a host with no D-Bus fails this way on every job it ever runs, so only the first is worth a warning...
        Assert.That(_logger.Records.Count(r => r.Level == LogLevel.Warning), Is.EqualTo(1));

        // ...but the rest are still reported, in case a later one is a different failure than the one already warned about.
        Assert.That(_logger.Records.Count(r => r.Level == LogLevel.Debug), Is.EqualTo(2));
    }

    [Test]
    public async Task Dispose_AfterFailedAcquire_IsSafeAndIdempotent()
    {
        var inhibitor = new SleepInhibitor(_logger, UnreachableBus);

        var handle = await inhibitor.AcquireAsync("job-1");

        Assert.DoesNotThrow(() => handle.Dispose());
        Assert.DoesNotThrow(() => handle.Dispose());
    }

    [Test]
    public async Task AcquireAsync_RealLogind_HoldsInhibitorAndReleasesOnDispose()
    {
        if (!HasSystemBus() || !HasSystemdInhibitCli())
        {
            Assert.Ignore("No logind system bus (or no systemd-inhibit CLI) on this host.");
        }

        var reason = $"ilmarinen-test-{Guid.NewGuid():N}";
        var inhibitor = new SleepInhibitor(_logger, busAddress: null);

        var handle = await inhibitor.AcquireAsync(reason);
        try
        {
            var held = ListInhibitors();
            Assert.That(held, Does.Contain(reason), "the inhibitor should be registered with logind while held");

            var line = held.Split('\n').First(l => l.Contains(reason));
            // "sleep" is the load-bearing half: a desktop's power manager auto-suspends by calling logind's Suspend(), which consults sleep inhibitors and ignores idle ones. "idle" covers a headless host's logind IdleAction.
            Assert.That(line, Does.Contain("sleep"));
            Assert.That(line, Does.Contain("idle"));
            Assert.That(line, Does.Contain("block"));
        }
        finally
        {
            handle.Dispose();
        }

        Assert.That(ListInhibitors(), Does.Not.Contain(reason), "disposing the handle should close the fd and release the inhibitor");
        Assert.That(_logger.Records.Any(r => r.Level == LogLevel.Warning), Is.False);
    }

    private static bool HasSystemBus()
    {
        return File.Exists("/run/dbus/system_bus_socket") || File.Exists("/var/run/dbus/system_bus_socket");
    }

    private static bool HasSystemdInhibitCli()
    {
        try
        {
            return ListInhibitors().Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string ListInhibitors()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "systemd-inhibit",
            ArgumentList = { "--list" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // systemd elides columns to fit the terminal; without this the WHY column can be truncated away.
            Environment = { ["COLUMNS"] = "500" }
        })!;

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Records { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Records.Add((logLevel, formatter(state, exception)));
        }
    }
}
