using Ilmarinen.Worker.Services;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;
using Tmds.DBus.Protocol;

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

    // Sleep inhibition is a nice-to-have, so an absent bus is not a warning — nothing is broken, and plenty of good hosts have no D-Bus. The worker's host_sleep_inhibit diagnostic step is where this is actually reported.
    [Test]
    public async Task AcquireAsync_BusUnreachable_DoesNotThrowAndReportsWithoutWarning()
    {
        var inhibitor = new SleepInhibitor(_logger, UnreachableBus);

        using var handle = await inhibitor.AcquireAsync("job-1");

        Assert.That(handle, Is.Not.Null);
        Assert.That(_logger.Records.Any(r => r.Level >= LogLevel.Warning), Is.False, "an optional capability being absent must not read as a problem");

        var reported = _logger.Records.Where(r => r.Level == LogLevel.Information).ToList();
        Assert.That(reported, Has.Count.EqualTo(1));
        Assert.That(reported[0].Message, Does.Contain("host_sleep_inhibit"), "the log should point at the diagnostic step that explains it");
    }

    [Test]
    public async Task AcquireAsync_BusUnreachable_ReportsOnceThenLogsQuietly()
    {
        var inhibitor = new SleepInhibitor(_logger, UnreachableBus);

        using (await inhibitor.AcquireAsync("job-1")) { }
        using (await inhibitor.AcquireAsync("job-2")) { }
        using (await inhibitor.AcquireAsync("job-3")) { }

        // A worker on a host with no D-Bus fails this way on every job it ever runs, so only the first is worth printing...
        Assert.That(_logger.Records.Count(r => r.Level == LogLevel.Information), Is.EqualTo(1));

        // ...but the rest are still reported, in case a later one is a different failure than the one already reported.
        Assert.That(_logger.Records.Count(r => r.Level == LogLevel.Debug), Is.EqualTo(2));
    }

    [Test]
    public async Task ProbeAsync_BusUnreachable_ReportsUnavailableAndDoesNotThrow()
    {
        var inhibitor = new SleepInhibitor(_logger, UnreachableBus);

        var step = await inhibitor.ProbeAsync(CancellationToken.None);

        Assert.That(step.Success, Is.False);
        Assert.That(step.Message, Does.Contain("not inhibited"));
        Assert.That(step.Suggestion, Does.Contain("Bind-mount"));

        // The probe reports; it does not log. The step it returns is the report.
        Assert.That(_logger.Records, Is.Empty);
    }

    [Test]
    public async Task ProbeAsync_RealLogind_ReportsAvailable()
    {
        if (!HasSystemBus() || !HasSystemdInhibitCli())
        {
            Assert.Ignore("No logind system bus (or no systemd-inhibit CLI) on this host.");
        }

        var step = await new SleepInhibitor(_logger, busAddress: null).ProbeAsync(CancellationToken.None);

        if (!step.Success)
        {
            // No reachable bus, or a sessionless non-root caller logind refuses — either way this host cannot hold an inhibitor, so there is nothing to verify. Workers run as root against the host bus and are unaffected.
            Assert.Ignore($"Cannot hold an inhibitor on this host: {step.Message}");
        }

        Assert.That(step.Suggestion, Is.Null, "there is nothing to suggest when the capability is present");
    }

    // Cancellation is a shutdown, not a failure to report. The diagnostic harness deliberately lets it propagate, so
    // turning it into a failed step would have the worker announce a bogus diagnostic on its way out the door.
    [Test]
    public void ProbeAsync_Cancelled_PropagatesRatherThanReportingAFailure()
    {
        var inhibitor = new SleepInhibitor(_logger, UnreachableBus);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.CatchAsync<OperationCanceledException>(async () => await inhibitor.ProbeAsync(cts.Token));
    }

    // The advice differs entirely by cause: telling a non-root worker to bind-mount a socket it already has is useless.
    [Test]
    public void Suggestion_DependsOnWhyItFailed()
    {
        var refused = new DBusErrorReplyException("org.freedesktop.DBus.Error.InteractiveAuthorizationRequired", "Access denied");
        Assert.That(SleepInhibitor.SuggestionFor(refused), Does.Contain("root"));
        Assert.That(SleepInhibitor.SuggestionFor(refused), Does.Not.Contain("Bind-mount"));

        var unreachable = new DBusConnectFailedException("Cannot assign requested address");
        Assert.That(SleepInhibitor.SuggestionFor(unreachable), Does.Contain("Bind-mount"));
        Assert.That(SleepInhibitor.SuggestionFor(unreachable), Does.Contain("system_bus_socket"));

        // Nothing useful to say beats a hint that isn't one.
        Assert.That(SleepInhibitor.SuggestionFor(new InvalidOperationException("something else")), Is.Null);
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
            IgnoreIfInhibitorNotAvailableHere();

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
        Assert.That(_logger.Records, Is.Empty, "a successful acquire has nothing to report");
    }

    /// <summary>
    /// The real-logind path is only testable where this process can actually hold an inhibitor, which many normal environments can't: no reachable bus (a CI runner whose socket is present but dead), or logind denying us for lack of a session or privilege. Those aren't failures — real workers run as root against the host bus and are unaffected — so skip, and let the success path below be the verification of the wire format and fd handoff. We can't be pickier: D-Bus checks policy per (interface, member) before dispatch, so a malformed request and a genuine denial both come back as AccessDenied — the error can't tell "our message is wrong" from "this host won't let us", and only a run that *succeeds* proves the message is right.
    /// </summary>
    private void IgnoreIfInhibitorNotAvailableHere()
    {
        var failure = _logger.Records.Select(r => r.Exception).FirstOrDefault(e => e != null);
        if (failure != null)
        {
            Assert.Ignore($"Cannot hold an inhibitor on this host, so there is nothing to verify: {failure.Message}");
        }
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
        public List<(LogLevel Level, string Message, Exception? Exception)> Records { get; } = new();

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
            Records.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
