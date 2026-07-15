using Ilmarinen.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using System.Threading.Tasks;
using System.Threading;
using System;
using Tmds.DBus.Protocol;

namespace Ilmarinen.Worker.Services;

/// <summary>
/// Keeps the host awake while a job runs, by holding a logind inhibitor.
///
/// logind's Inhibit() hands back a file descriptor, and the inhibitor lives for exactly as long as that fd stays open — so we hold it in-process and close it when the job ends. A containerized worker can do this against the host's logind provided the host's D-Bus system bus socket is bind-mounted in: the fd arrives over SCM_RIGHTS, which crosses the namespace boundary fine.
///
/// We inhibit "idle" and "sleep" together, because different hosts suspend by different routes. A headless box suspends via logind's own IdleAction, which only an "idle" inhibitor gates. A desktop suspends via its power manager (KDE's PowerDevil, GNOME's gsd-power), which never reads logind's idle inhibitors at all — it calls logind's Suspend(), which only a "sleep" inhibitor gates. Inhibiting just one of the two is a no-op on half the fleet.
///
/// Consequence worth knowing: a desktop power manager also routes lid-close through Suspend(), so a running job will keep a lid-shut laptop awake. On a headless host the lid still wins, because logind's LidSwitchIgnoreInhibited defaults to yes.
///
/// This needs the worker to run as root, and the worker container does. logind gates block-sleep behind polkit's auth_admin_keep for callers with no logind session, and a container is always sessionless — uid 0 is what bypasses the check. A "sleep" request from a sessionless non-root process is refused outright, and refused as a whole rather than downgraded to the "idle" it would have been allowed. So adding a USER directive to the worker's Dockerfile would silently turn this feature off.
/// </summary>
public class SleepInhibitor
{
    // The bus is a local unix socket, so this only trips on a wedged dbus-daemon: the socket is listened on by dbus.socket, so connect() succeeds and the handshake then never completes. Without a bound wait that would hang a job forever.
    private static readonly TimeSpan AcquireTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<SleepInhibitor> _logger;
    private readonly string? _busAddress;
    private bool _reported;

    public SleepInhibitor(ILogger<SleepInhibitor> logger, string? busAddress = null)
    {
        _logger = logger;
        _busAddress = busAddress ?? DBusAddress.System;
    }

    /// <summary>
    /// Takes an inhibitor for the duration of the returned handle. Dispose it to release.
    ///
    /// Never throws. Sleep inhibition is best-effort — plenty of perfectly good hosts have no D-Bus to talk to — and a job must never fail over it.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(string reason)
    {
        var (handle, failure) = await TryAcquireAsync(reason, CancellationToken.None);

        if (failure != null)
        {
            ReportFailure(failure);
        }

        return handle;
    }

    /// <summary>
    /// Takes an inhibitor and immediately releases it, reporting as a diagnostic step whether this host can inhibit
    /// sleep at all. A real attempt rather than a guess at the preconditions: only logind can say whether it will hand
    /// us the fd. Shaped to drop straight into the diagnostic's step harness, which stamps the name and duration.
    ///
    /// Never logs — the step it returns is the report. Never throws, except to honour cancellation.
    /// </summary>
    public async Task<DiagnosticStepResult> ProbeAsync(CancellationToken ct)
    {
        var (handle, failure) = await TryAcquireAsync("Ilmarinen worker self-check", ct);
        handle.Dispose();

        if (failure == null)
        {
            return new DiagnosticStepResult
            {
                Name = "",
                Success = true,
                Message = "The host will be kept awake while a job runs."
            };
        }

        return new DiagnosticStepResult
        {
            Name = "",
            Success = false,
            Message = $"Host sleep is not inhibited: {failure.Message}",
            Suggestion = SuggestionFor(failure)
        };
    }

    /// <summary>
    /// What to do about a failure to inhibit, which depends entirely on why. Telling a non-root worker to bind-mount a
    /// socket it already has would be useless advice, so the causes are not collapsed into one "unavailable".
    /// </summary>
    public static string? SuggestionFor(Exception failure)
    {
        // logind refuses a block-sleep inhibitor to any caller that has no logind session and is not uid 0, and says so with this error rather than by failing the connection.
        if (failure is DBusErrorReplyException reply && reply.ErrorName == "org.freedesktop.DBus.Error.InteractiveAuthorizationRequired")
        {
            return "The worker must run as root: logind refuses a block-sleep inhibitor to a caller that is neither root nor in a logind session.";
        }

        if (failure is DBusConnectFailedException)
        {
            return "Bind-mount /run/dbus/system_bus_socket into the worker container (Linux hosts only) — see deploy/worker-docker/docker-compose.inhibit-sleep.yml. On SELinux hosts the container may be denied access; do not use :z/:Z on the bus socket, which would break D-Bus for the whole host.";
        }

        return null;
    }

    private async Task<(IDisposable Handle, Exception? Failure)> TryAcquireAsync(string reason, CancellationToken ct)
    {
        // Up front, so cancellation doesn't have to win a race against the connection failing on its own: on a host with no bus, the connect faults fast enough to be reported as a failure instead.
        ct.ThrowIfCancellationRequested();

        DBusConnection? connection = null;
        try
        {
            connection = new DBusConnection(_busAddress!);
            await connection.ConnectAsync().AsTask().WaitAsync(AcquireTimeout, ct);

            var fd = await connection.CallMethodAsync(
                CreateInhibitMessage(connection, reason),
                (Message message, object? state) =>
                {
                    return message.GetBodyReader().ReadHandle<SafeFileHandle>();
                },
                null).WaitAsync(AcquireTimeout, ct);

            return (new InhibitorHandle(connection, fd), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation is not a failure to report, it's a shutdown: the diagnostic harness relies on it propagating rather than being turned into a failed step. AcquireAsync passes no token, so its never-throws contract is untouched. A blown AcquireTimeout surfaces as TimeoutException, not this, so the two don't collide.
            connection?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            connection?.Dispose();
            return (new InhibitorHandle(connection: null, fd: null), ex);
        }
    }

    // Qualified because Ilmarinen.Worker.Services has its own MessageBuffer (the SignalR replay buffer).
    private static Tmds.DBus.Protocol.MessageBuffer CreateInhibitMessage(DBusConnection connection, string reason)
    {
        using var writer = connection.GetMessageWriter();

        writer.WriteMethodCallHeader(
            destination: "org.freedesktop.login1",
            path: "/org/freedesktop/login1",
            @interface: "org.freedesktop.login1.Manager",
            member: "Inhibit",
            signature: "ssss");
        writer.WriteString("idle:sleep");
        writer.WriteString("Ilmarinen worker");
        writer.WriteString(reason);
        writer.WriteString("block");

        return writer.CreateMessage();
    }

    private void ReportFailure(Exception ex)
    {
        // Information, not Warning: nothing is broken. Plenty of hosts have no D-Bus and never sleep. The worker's diagnostic is where this is actually reported — see the host_sleep_inhibit step — so this is a breadcrumb, and only the first is worth printing. Later ones are still logged, in case a later failure differs from the one already reported.
        if (_reported)
        {
            _logger.LogDebug(ex, "Could not inhibit host sleep");
            return;
        }

        _reported = true;
        _logger.LogInformation(ex, "Host sleep is not being inhibited, so the host may suspend mid-job. See the worker's host_sleep_inhibit diagnostic step.");
    }

    private sealed class InhibitorHandle : IDisposable
    {
        private readonly DBusConnection? _connection;
        private readonly SafeFileHandle? _fd;

        public InhibitorHandle(DBusConnection? connection, SafeFileHandle? fd)
        {
            _connection = connection;
            _fd = fd;
        }

        public void Dispose()
        {
            // Closing the fd is what releases the inhibitor; the connection is just the pipe it arrived over. Both calls are idempotent, so a no-op handle (failed acquire) and a double dispose are equally fine.
            _fd?.Dispose();
            _connection?.Dispose();
        }
    }
}
