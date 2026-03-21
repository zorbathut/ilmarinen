using NUlid;

namespace Ilmarinen.Server;

public static class WorkerDisplay
{
    /// <summary>
    /// Returns the worker name if available, otherwise the first 8 characters of the ULID.
    /// Returns "-" if workerId is null.
    /// </summary>
    public static string FormatWorker(Ulid? workerId, string? workerName)
    {
        if (workerId == null) return "-";
        return !string.IsNullOrEmpty(workerName) ? workerName : workerId.Value.ToString()[..8];
    }
}
