using System.Collections.Generic;

namespace Ilmarinen.Protocol.Responses;

/// <summary>
/// Server → Client: Queued jobs no available worker can take, via HTTP GET /api/jobs/unsatisfiable. A worker is available if it is connected, not paused by an operator, not holding back (someone is using its machine, say), and either ready for work or running a job; these jobs need a higher priority than every available worker has, so they wait until a suitable worker becomes available.
/// </summary>
public record UnsatisfiableJobsReport
{
    /// <summary>
    /// Null when no worker is available at all, in which case every queued job is listed.
    /// </summary>
    public WorkerPriority? HighestAvailableWorkerPriority { get; init; }

    public required IReadOnlyList<JobInfo> Jobs { get; init; }

    /// <summary>
    /// Connected workers an operator has paused that, unpaused, would be available and could take at least one of these jobs. Empty when there are none.
    /// </summary>
    public required IReadOnlyList<string> PausedWorkers { get; init; }

    /// <summary>
    /// Connected workers held back because someone is using their machine that, left alone, would be available and could take at least one of these jobs. Empty when there are none.
    /// </summary>
    public required IReadOnlyList<string> InUseWorkers { get; init; }
}
