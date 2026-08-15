using System;

namespace Ilmarinen.WorkerLauncher;

/// <summary>
/// The relaunch rules: the manifest is the single source of truth and the child's exit code is only a hint, so a changed manifest hash always switches immediately, while relaunching the same bundle backs off — otherwise a worker that exits UpdateRequired against an unchanged manifest (persistent handshake failure) would hot-loop.
/// </summary>
public static class RelaunchPolicy
{
    // Must match WorkerExitCodes.UpdateRequired in Ilmarinen.Worker; the launcher deliberately references no Ilmarinen projects.
    public const int UpdateRequiredExitCode = 42;

    public static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(2);

    /// <summary>A child that ran at least this long was working; relaunch it without backoff.</summary>
    public static readonly TimeSpan StableUptime = TimeSpan.FromMinutes(5);

    public static bool ShouldSwitch(string? lastRunHash, string nextRunHash)
    {
        return lastRunHash != null && nextRunHash != lastRunHash;
    }

    public static TimeSpan NextBackoff(TimeSpan current)
    {
        if (current == TimeSpan.Zero)
        {
            return InitialBackoff;
        }

        var doubled = current * 2;
        return doubled > MaxBackoff ? MaxBackoff : doubled;
    }
}
