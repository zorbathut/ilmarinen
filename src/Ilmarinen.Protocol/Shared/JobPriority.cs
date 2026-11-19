namespace Ilmarinen.Protocol;

/// <summary>
/// Which queued job dispatch takes first: the highest priority a worker qualifies for, oldest first among equals. It orders the queue and nothing else; a running job is never pre-empted for a higher one. The values only need to ascend with priority. Medium is zero so that it is what an omitted field, an existing row or an unset property comes out as; this deliberately differs from WorkerPriority's 0..2, so a raw-JSON client must not reuse WorkerPriority's numbers here.
/// </summary>
public enum JobPriority
{
    Low = -1,
    Medium = 0,
    High = 1
}
