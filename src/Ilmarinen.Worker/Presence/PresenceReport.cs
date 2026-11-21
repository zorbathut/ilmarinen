using Ilmarinen.Protocol;
using Ilmarinen.Worker.Host;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.Worker.Presence;

/// <summary>
/// Whether the in-use hold (<see cref="PresenceService"/>) is working, for the worker's diagnostic: what <see cref="PresenceService"/> last recorded about the devices it reads and its verdict, set beside the keyboards and mice the host has, and whether logind answers.
/// </summary>
public sealed class PresenceReport
{
    private readonly Logind _logind;
    private readonly WorkerConfig _config;
    private readonly string _deviceList;
    private volatile Recorded? _recorded;

    private sealed record Recorded(IReadOnlyList<InputDevice> Reading, PresenceVerdict? Verdict);

    public PresenceReport(Logind logind, WorkerConfig config, string deviceList = InputDevices.ListPath)
    {
        _logind = logind;
        _config = config;
        _deviceList = deviceList;
    }

    /// <summary>Called by <see cref="PresenceService"/> each time it looks: the devices it is reading, and its verdict (null before its first).</summary>
    public void Record(IReadOnlyList<InputDevice> reading, PresenceVerdict? verdict)
    {
        _recorded = new Recorded(reading, verdict);
    }

    public async Task<DiagnosticStepResult> ProbeAsync(CancellationToken ct)
    {
        IReadOnlyList<InputDevice>? hostDevices;
        try
        {
            hostDevices = InputDevices.Parse(await File.ReadAllTextAsync(_deviceList, ct));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            hostDevices = null;
        }

        Exception? logindFailure = null;
        try
        {
            await _logind.ListSessionsAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logindFailure = ex;
        }

        var recorded = _recorded;
        return Describe(hostDevices, recorded?.Reading, recorded?.Verdict, logindFailure, _config.InUseIdleTime);
    }

    /// <param name="hostDevices">The host's keyboards and mice; null if they couldn't be listed.</param>
    /// <param name="reading">The ones the worker reads; null if it hasn't looked yet.</param>
    public static DiagnosticStepResult Describe(IReadOnlyList<InputDevice>? hostDevices, IReadOnlyList<InputDevice>? reading, PresenceVerdict? verdict, Exception? logindFailure, TimeSpan idleTime)
    {
        if (reading == null)
        {
            return Result(false, "The in-use watch hasn't started yet.", null);
        }

        if (reading.Count == 0)
        {
            if (hostDevices is { Count: 0 })
            {
                return Result(true, "This host has no keyboard or mouse, so nobody can be using it: the worker never holds back for that.", null);
            }

            var found = hostDevices == null ? "the host's input devices couldn't be listed" : $"the host has {hostDevices.Count} ({Names(hostDevices)})";
            return Result(false, $"No keyboard or mouse is readable ({found}), so the worker takes jobs even while someone is using this machine.", "If people use this machine, add the in-use compose overlay (docker-compose.in-use.yml beside the worker's docker-compose.yml), which mounts /dev/input read-only; a worker outside a container needs read access to /dev/input/event*.");
        }

        var now = verdict == null ? "" : verdict.InUse ? $" Now: in use ({verdict.Reason})." : $" Now: not in use ({verdict.Reason}).";
        var watching = $"Watching {reading.Count} device(s) ({Names(reading)}); idle after {idleTime.TotalMinutes:0} minutes.{now}";
        if (logindFailure != null)
        {
            return Result(false, $"{watching} logind can't be reached ({logindFailure.Message}), so the screen lock and programs keeping the machine awake are ignored: only input counts.", "Mount the host's D-Bus socket too (docker-compose.inhibit-sleep.yml, used together with docker-compose.in-use.yml).");
        }
        return Result(true, watching, null);
    }

    private static string Names(IReadOnlyList<InputDevice> devices)
    {
        return string.Join(", ", devices.Select(d => d.Name));
    }

    private static DiagnosticStepResult Result(bool success, string message, string? suggestion)
    {
        return new DiagnosticStepResult { Name = "", Success = success, Message = message, Suggestion = suggestion };
    }
}
