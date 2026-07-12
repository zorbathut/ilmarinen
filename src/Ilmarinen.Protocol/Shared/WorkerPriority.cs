namespace Ilmarinen.Protocol;

/// <summary>
/// Dispatch preference for a worker. The numeric values are the sort key — dispatch picks the ready worker with the highest one — so keep them ascending with preference. Ties are broken arbitrarily; there is no balancing within a level.
/// </summary>
public enum WorkerPriority
{
    Low = 0,
    Medium = 1,
    High = 2
}
