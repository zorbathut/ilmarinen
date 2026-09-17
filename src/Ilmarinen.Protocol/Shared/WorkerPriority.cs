namespace Ilmarinen.Protocol;

/// <summary>
/// Dispatch preference for a worker, and the tier a job's minimum worker priority is measured against. The numeric values are both the sort key — dispatch picks the ready worker with the highest one — and the threshold — a worker may run a job only if its value is at least the job's minimum — so keep them ascending with preference. Ties are broken arbitrarily; there is no balancing within a level.
/// </summary>
public enum WorkerPriority
{
    Low = 0,
    Medium = 1,
    High = 2
}
