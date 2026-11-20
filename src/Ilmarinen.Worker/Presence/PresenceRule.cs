using Ilmarinen.Worker.Host;
using System.Collections.Generic;
using System.Linq;
using System;

namespace Ilmarinen.Worker.Presence;

/// <summary>Whether a person is using the machine, and the reason, for the log.</summary>
public sealed record PresenceVerdict(bool InUse, string Reason);

/// <summary>
/// A person is using the machine if there has been keyboard or mouse input lately, or another program holds a logind inhibitor keeping it awake (a video or a call, where the desktop passes the application's request on to logind), unless the screen is locked: locking it says the person has gone, whatever is still running.
/// </summary>
public static class PresenceRule
{
    /// <summary>The worker's own inhibitors are made under this name: a job keeping the host awake says nothing about a person.</summary>
    public const string OwnInhibitorWho = "Ilmarinen worker";

    public static PresenceVerdict Judge(DateTimeOffset? lastInput, string? lastInputDevice, TimeSpan idleTime, DateTimeOffset now, bool locked, IReadOnlyList<LogindInhibitor> inhibitors)
    {
        if (locked)
        {
            return new PresenceVerdict(false, "the screen is locked, or nobody is logged in at it");
        }
        if (lastInput is { } at && now - at < idleTime)
        {
            return new PresenceVerdict(true, $"input from {lastInputDevice ?? "a device"} at {at:u}");
        }
        if (inhibitors.FirstOrDefault(KeepsTheMachineAwake) is { } inhibitor)
        {
            return new PresenceVerdict(true, $"{inhibitor.Who} is keeping the machine awake ({inhibitor.Why})");
        }
        return new PresenceVerdict(false, $"no input for {idleTime.TotalMinutes:0} minutes");
    }

    /// <summary>
    /// A block inhibitor on idle or sleep that isn't the worker's own. Delay inhibitors only hold suspend up for a moment of tidying (NetworkManager, Discord), and handle-* ones are a desktop claiming the power keys; neither says anyone is there.
    /// </summary>
    public static bool KeepsTheMachineAwake(LogindInhibitor inhibitor)
    {
        return inhibitor.Mode == "block"
            && inhibitor.Who != OwnInhibitorWho
            && inhibitor.What.Split(':').Any(w => w is "idle" or "sleep");
    }

    /// <summary>
    /// Whether the machine's screen is locked: the active graphical session at seat0 says so. With no such session (only a login screen, or nobody logged in) nobody is at the machine, which counts as locked.
    /// </summary>
    public static bool IsLocked(IReadOnlyList<LogindSession> sessions)
    {
        var at = sessions.FirstOrDefault(s => s is { Seat: "seat0", Class: "user", Active: true, Type: "x11" or "wayland" });
        return at == null || at.LockedHint;
    }
}
