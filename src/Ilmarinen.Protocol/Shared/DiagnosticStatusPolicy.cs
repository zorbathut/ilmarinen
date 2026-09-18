namespace Ilmarinen.Protocol;

/// <summary>
/// What a reported status means for a worker's ability to take work. Shared because both ends act on it: the worker
/// decides whether to ask for jobs, and the server decides whether to believe it.
/// </summary>
public static class DiagnosticStatusPolicy
{
    /// <summary>
    /// Degraded means the capability checks passed but the diagnostic left something behind, so the worker can still run jobs. Stated as an allowlist so the transient Running placeholder is never mistaken for readiness.
    /// </summary>
    public static bool CanAcceptJobs(DiagnosticStatus status)
    {
        return status == DiagnosticStatus.Healthy || status == DiagnosticStatus.Degraded;
    }
}
