using Microsoft.Win32.SafeHandles;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System;
using Tmds.DBus.Protocol;

namespace Ilmarinen.Worker.Host;

/// <summary>A logind inhibitor, as ListInhibitors reports it.</summary>
public sealed record LogindInhibitor(string What, string Who, string Why, string Mode, uint Uid, uint Pid);

/// <summary>A logind session, with the properties that say whether a person is at it.</summary>
public sealed record LogindSession(string Id, string Seat, string Class, string Type, bool Active, bool LockedHint);

/// <summary>
/// The host's logind, over the system bus: its sessions, its inhibitors, and its PrepareForSleep signal. One connection, made on first use and made again after it drops; a containerized worker reaches the host's logind through the bind-mounted bus socket (deploy/worker-docker/docker-compose.inhibit-sleep.yml).
/// </summary>
public sealed class Logind : IDisposable
{
    // As SleepInhibitor's: the bus is a local socket, so this only trips on a wedged dbus-daemon.
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    private const string Service = "org.freedesktop.login1";
    private const string ManagerPath = "/org/freedesktop/login1";
    private const string ManagerInterface = "org.freedesktop.login1.Manager";

    private readonly string _busAddress;
    private readonly SemaphoreSlim _connect = new(1, 1);
    private DBusConnection? _connection;

    public Logind(string? busAddress = null)
    {
        _busAddress = busAddress ?? DBusAddress.System!;
    }

    public async Task<IReadOnlyList<LogindInhibitor>> ListInhibitorsAsync(CancellationToken ct)
    {
        var connection = await ConnectAsync(ct);
        return await connection.CallMethodAsync(ManagerCall(connection, "ListInhibitors"), (Message message, object? state) =>
        {
            var reader = message.GetBodyReader();
            var inhibitors = new List<LogindInhibitor>();
            var end = reader.ReadArrayStart(DBusType.Struct);
            while (reader.HasNext(end))
            {
                reader.AlignStruct();
                inhibitors.Add(new LogindInhibitor(reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadUInt32(), reader.ReadUInt32()));
            }
            return inhibitors;
        }, null).WaitAsync(CallTimeout, ct);
    }

    public async Task<IReadOnlyList<LogindSession>> ListSessionsAsync(CancellationToken ct)
    {
        var connection = await ConnectAsync(ct);
        var listed = await connection.CallMethodAsync(ManagerCall(connection, "ListSessions"), (Message message, object? state) =>
        {
            var reader = message.GetBodyReader();
            var sessions = new List<(string Id, string Seat, string Path)>();
            var end = reader.ReadArrayStart(DBusType.Struct);
            while (reader.HasNext(end))
            {
                reader.AlignStruct();
                var id = reader.ReadString();
                reader.ReadUInt32();
                reader.ReadString();
                var seat = reader.ReadString();
                sessions.Add((id, seat, reader.ReadObjectPathAsString()));
            }
            return sessions;
        }, null).WaitAsync(CallTimeout, ct);

        var result = new List<LogindSession>();
        foreach (var (id, seat, path) in listed)
        {
            var properties = await connection.CallMethodAsync(GetAllCall(connection, path), (Message message, object? state) =>
            {
                return message.GetBodyReader().ReadDictionaryOfStringToVariantValue();
            }, null).WaitAsync(CallTimeout, ct);

            result.Add(new LogindSession(
                id,
                seat,
                properties.TryGetValue("Class", out var cls) ? cls.GetString() : "",
                properties.TryGetValue("Type", out var type) ? type.GetString() : "",
                properties.TryGetValue("Active", out var active) && active.GetBool(),
                properties.TryGetValue("LockedHint", out var locked) && locked.GetBool()));
        }
        return result;
    }

    /// <summary>Takes an inhibitor, held for as long as the returned handle stays open.</summary>
    public async Task<SafeFileHandle> InhibitAsync(string what, string who, string why, string mode, CancellationToken ct)
    {
        var connection = await ConnectAsync(ct);
        return await connection.CallMethodAsync(InhibitCall(connection, what, who, why, mode), (Message message, object? state) =>
        {
            return message.GetBodyReader().ReadHandle<SafeFileHandle>()!;
        }, null).WaitAsync(CallTimeout, ct);
    }

    /// <summary>Calls <paramref name="handler"/> with PrepareForSleep's argument: true as the host is about to sleep, false once it has woken. Returns the connection's end, so the caller can watch again on a new one.</summary>
    public async Task<Task<Exception?>> WatchPrepareForSleepAsync(Action<bool> handler, Action<Exception> onError, CancellationToken ct)
    {
        var connection = await ConnectAsync(ct);
        await connection.WatchSignalAsync(Service, ManagerPath, ManagerInterface, "PrepareForSleep", (Message message, object? state) =>
        {
            return message.GetBodyReader().ReadBool();
        }, (Exception? ex, bool sleeping) =>
        {
            if (ex != null)
            {
                onError(ex);
                return;
            }
            handler(sleeping);
        }, null, false, ObserverFlags.None).AsTask().WaitAsync(CallTimeout, ct);
        return connection.DisconnectedAsync();
    }

    /// <summary>Drops the connection, ending any watch on it; the next call makes a new one.</summary>
    public void Reset()
    {
        _connection?.Dispose();
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connect.Dispose();
    }

    private async Task<DBusConnection> ConnectAsync(CancellationToken ct)
    {
        await _connect.WaitAsync(ct);
        try
        {
            if (_connection != null && !_connection.DisconnectedAsync().IsCompleted)
            {
                return _connection;
            }

            _connection?.Dispose();
            _connection = null;
            var connection = new DBusConnection(_busAddress);
            try
            {
                await connection.ConnectAsync().AsTask().WaitAsync(CallTimeout, ct);
            }
            catch
            {
                connection.Dispose();
                throw;
            }
            _connection = connection;
            return connection;
        }
        finally
        {
            _connect.Release();
        }
    }

    // Qualified because Ilmarinen.Worker.Services has its own MessageBuffer (the SignalR replay buffer).
    private static Tmds.DBus.Protocol.MessageBuffer ManagerCall(DBusConnection connection, string member)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Service, path: ManagerPath, @interface: ManagerInterface, member: member);
        return writer.CreateMessage();
    }

    private static Tmds.DBus.Protocol.MessageBuffer InhibitCall(DBusConnection connection, string what, string who, string why, string mode)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Service, path: ManagerPath, @interface: ManagerInterface, member: "Inhibit", signature: "ssss");
        writer.WriteString(what);
        writer.WriteString(who);
        writer.WriteString(why);
        writer.WriteString(mode);
        return writer.CreateMessage();
    }

    private static Tmds.DBus.Protocol.MessageBuffer GetAllCall(DBusConnection connection, string path)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Service, path: path, @interface: "org.freedesktop.DBus.Properties", member: "GetAll", signature: "s");
        writer.WriteString("org.freedesktop.login1.Session");
        return writer.CreateMessage();
    }
}
