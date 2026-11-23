using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;

namespace Ilmarinen.Server.Components;

/// <summary>
/// How a worker's health renders in the connection badge. Shared by the workers list and the worker detail
/// page: the two must agree, and they can't if each keeps its own copy.
/// </summary>
public static class WorkerBadge
{
    public static string GetBadgeClass(WorkerView w)
    {
        if (!w.IsConnected)
        {
            return w.IsWaking ? "waking" : "disconnected";
        }
        if (w.Diagnostic?.Status == DiagnosticStatus.Unhealthy)
        {
            return "unhealthy";
        }
        if (w.IsPaused)
        {
            return "paused";
        }
        if (w.Hold != null && !w.HoldWaived)
        {
            return "held";
        }
        if (w.NoRoom != null)
        {
            return "blocked";
        }
        if (w.Diagnostic?.Status == DiagnosticStatus.Degraded)
        {
            return "degraded";
        }
        return "connected";
    }

    public static string GetStatusLabel(WorkerView w)
    {
        // The pause rides along on whatever else the worker is, since it is the one thing about it the operator set and can undo. An idle paused worker reads "Paused" even if something else is also holding it back: the pause is the blocker the operator can see and clear, and the label after unpausing shows any other.
        var paused = w.IsPaused ? ", paused" : "";
        if (!w.IsConnected)
        {
            var asleep = w.IsWaking ? "Waking…" : w.WakeFailedAt is { } at ? $"Offline, didn't wake at {at.UtcDateTime:HH:mm}" : "Offline";
            return $"{asleep}{paused}{(w.NoRoom != null ? $", {Blocked.ToLowerInvariant()}" : "")}";
        }
        if (w.Diagnostic?.Status == DiagnosticStatus.Unhealthy)
        {
            return $"Unhealthy{paused}";
        }

        // A degraded worker still takes jobs, so Ready/Busy — the thing an operator actually scans this column
        // for — has to survive; degraded rides along rather than replacing it.
        // Not ready without being busy is an idle worker the server is holding back: a host re-check owed, a bundle update pending, or a reconnect not yet resumed.
        // A hold the worker's mode waives (someone at the machine, and it is set to take jobs even so) is a note on the label, not a reason it isn't ready.
        var busy = w.CurrentJobs.Count > 0 || w.Diagnostic?.Status == DiagnosticStatus.Running;
        var hold = w.Hold is { } h && !w.HoldWaived ? HoldLabel(h) : null;
        var label = w.IsReady ? (w.NoRoom != null ? Blocked : "Ready")
            : busy ? $"Busy{paused}{(hold != null ? $", {hold.ToLowerInvariant()}" : "")}"
            : w.IsPaused ? "Paused"
            : hold ?? "Not ready";
        if (w.HoldWaived)
        {
            label += " (in use)";
        }
        else if (w.Hold == WorkerHold.InUse && w.Mode == WorkerMode.EvenWhileInUse && w.SpeaksTooOldAProtocolToTakeJobsWhileInUse)
        {
            label += " (needs a newer worker to take jobs anyway)";
        }
        if (w.Diagnostic?.Status == DiagnosticStatus.Degraded)
        {
            return $"{label} (degraded)";
        }
        return label;
    }

    /// <summary>Why a worker reads Blocked, and what to do about it; null for one that doesn't.</summary>
    public static string? GetStatusDetail(WorkerView w)
    {
        if (w.NoRoom is not { } noRoom)
        {
            return null;
        }
        var jobs = noRoom.Jobs == 1 ? "the queued job" : $"any of the {noRoom.Jobs} queued jobs";
        return $"No room for {jobs} it could take. Freeing {DisplayFormat.Size(noRoom.Gap.ShortBytes)} on it would let it take one (it needs {DisplayFormat.Size(noRoom.Gap.NeededBytes)} beyond the floor it keeps). Of its disk, {DisplayFormat.Size(noRoom.OtherUseBytes)} is neither free, workspaces nor git mirrors: the host's own files, space the filesystem reserves, and on a containerized worker usually Docker's images and build cache.";
    }

    private const string Blocked = "Blocked: no room";

    private static string HoldLabel(WorkerHold hold)
    {
        return hold switch
        {
            WorkerHold.InUse => "In use",
            WorkerHold.Sleeping => "Sleeping",
            WorkerHold.ShuttingDown => "Stopping",
            _ => hold.ToString()
        };
    }
}
