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
            return "disconnected";
        }
        if (w.Diagnostic?.Status == DiagnosticStatus.Unhealthy)
        {
            return "unhealthy";
        }
        if (w.IsPaused)
        {
            return "paused";
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
            return $"Offline{paused}";
        }
        if (w.Diagnostic?.Status == DiagnosticStatus.Unhealthy)
        {
            return $"Unhealthy{paused}";
        }

        // A degraded worker still takes jobs, so Ready/Busy — the thing an operator actually scans this column
        // for — has to survive; degraded rides along rather than replacing it.
        // Not ready without being busy is an idle worker the server is holding back: a host re-check owed, a bundle update pending, or a reconnect not yet resumed.
        var busy = w.CurrentJobs.Count > 0 || w.Diagnostic?.Status == DiagnosticStatus.Running;
        var label = w.IsReady ? "Ready" : busy ? $"Busy{paused}" : w.IsPaused ? "Paused" : "Not ready";
        if (w.Diagnostic?.Status == DiagnosticStatus.Degraded)
        {
            return $"{label} (degraded)";
        }
        return label;
    }
}
