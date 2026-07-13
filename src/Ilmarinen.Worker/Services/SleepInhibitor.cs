using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using System.Threading.Tasks;
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
/// </summary>
public class SleepInhibitor
{
    // The bus is a local unix socket, so this only trips on a wedged dbus-daemon: the socket is listened on by dbus.socket, so connect() succeeds and the handshake then never completes. Without a bound wait that would hang a job forever.
    private static readonly TimeSpan AcquireTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<SleepInhibitor> _logger;
    private readonly string? _busAddress;
    private bool _warned;

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
        DBusConnection? connection = null;
        try
        {
            connection = new DBusConnection(_busAddress!);
            await connection.ConnectAsync().AsTask().WaitAsync(AcquireTimeout);

            var fd = await connection.CallMethodAsync(
                CreateInhibitMessage(connection, reason),
                (Message message, object? state) =>
                {
                    return message.GetBodyReader().ReadHandle<SafeFileHandle>();
                },
                null).WaitAsync(AcquireTimeout);

            return new InhibitorHandle(connection, fd);
        }
        catch (Exception ex)
        {
            connection?.Dispose();
            ReportFailure(ex);
            return new InhibitorHandle(connection: null, fd: null);
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
        // A worker on a host with no D-Bus fails this way on every job it ever runs, so only the first one is worth a warning. Later failures still get logged — they may be a different failure than the one already reported.
        if (_warned)
        {
            _logger.LogDebug(ex, "Could not inhibit host sleep");
            return;
        }

        _warned = true;
        _logger.LogWarning(ex, "Could not inhibit host sleep; the host may suspend mid-job. To enable this, bind-mount /run/dbus/system_bus_socket into the worker container (Linux hosts only).");
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
