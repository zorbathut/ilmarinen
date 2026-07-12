namespace Ilmarinen.Protocol;

public enum DiagnosticStatus
{
    Unknown,
    Running,
    Healthy,
    Unhealthy,

    /// <summary>
    /// Every capability check passed — the worker can run jobs — but the diagnostic failed to tidy up after itself.
    /// </summary>
    Degraded
}
