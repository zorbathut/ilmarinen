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
        if (w.Diagnostic?.Status == DiagnosticStatus.Degraded)
        {
            return "degraded";
        }
        return "connected";
    }

    public static string GetStatusLabel(WorkerView w)
    {
        if (!w.IsConnected)
        {
            return "Offline";
        }
        if (w.Diagnostic?.Status == DiagnosticStatus.Unhealthy)
        {
            return "Unhealthy";
        }

        // A degraded worker still takes jobs, so Ready/Busy — the thing an operator actually scans this column
        // for — has to survive; degraded rides along rather than replacing it.
        // Not ready without being busy is an idle worker the server is holding back: a host re-check owed, a bundle update pending, or a reconnect not yet resumed.
        var label = w.IsReady ? "Ready" : w.CurrentJobs.Count > 0 || w.Diagnostic?.Status == DiagnosticStatus.Running ? "Busy" : "Not ready";
        if (w.Diagnostic?.Status == DiagnosticStatus.Degraded)
        {
            return $"{label} (degraded)";
        }
        return label;
    }
}
