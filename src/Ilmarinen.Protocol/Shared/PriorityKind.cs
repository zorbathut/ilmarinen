namespace Ilmarinen.Protocol;

/// <summary>
/// The two lists of priorities an operator keeps. A worker's priority orders dispatch among ready workers and is what a job's minimum is measured against; a job's priority orders the queue.
/// </summary>
public enum PriorityKind
{
    Worker,
    Job
}
