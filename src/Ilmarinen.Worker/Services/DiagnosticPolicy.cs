using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using System;

namespace Ilmarinen.Worker.Services;

/// <summary>
/// What a diagnostic report means for a worker: whether it can take jobs, and when to run another one.
/// </summary>
internal static class DiagnosticPolicy
{
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryCeiling = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Degraded means the capability checks passed but the diagnostic left something behind, so the worker can still run jobs. Stated as an allowlist so the transient Running placeholder is never mistaken for readiness.
    /// </summary>
    public static bool CanAcceptJobs(DiagnosticStatus status)
    {
        return status == DiagnosticStatus.Healthy || status == DiagnosticStatus.Degraded;
    }

    /// <summary>
    /// Whether to run another diagnostic on the worker's own initiative. A worker that can't take jobs is out of the fleet until some diagnostic says otherwise, and only this brings it back — but there's no point starting containers for a worker with nowhere to report the answer, or one that is draining and about to exit anyway.
    /// </summary>
    public static bool ShouldRetry(DiagnosticReport? lastReport, bool connected, bool draining)
    {
        return lastReport != null
            && !CanAcceptJobs(lastReport.Status)
            && connected
            && !draining;
    }

    /// <summary>
    /// How long to wait before the next attempt. The first wait is short because most failures are a blip — a resolver that stopped answering, a registry timeout, a daemon restart — and the wait climbs to a ceiling so a host that is genuinely broken isn't pulling images and building containers every few seconds for as long as it stays up.
    /// </summary>
    public static TimeSpan RetryDelay(int consecutiveFailures)
    {
        // The doublings are capped because the multiplication below overflows before the ceiling can bound it: Math.Pow(2, huge) is infinity, and a TimeSpan times infinity throws.
        var doublings = Math.Min(consecutiveFailures - 1, 16);
        var delay = FirstRetryDelay * Math.Pow(2, doublings);

        return delay < RetryCeiling ? delay : RetryCeiling;
    }

    /// <summary>Spreads a fleet that failed together — everything behind one resolver, say — so its workers don't all hit the registry again in the same second.</summary>
    public static TimeSpan Jitter(TimeSpan delay)
    {
        return delay * (0.8 + Random.Shared.NextDouble() * 0.4);
    }
}
