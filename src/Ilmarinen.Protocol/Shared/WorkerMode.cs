namespace Ilmarinen.Protocol;

/// <summary>What the operator has set a worker to do about new jobs; what it is running carries on either way. Stored by number, so the values are fixed.</summary>
public enum WorkerMode
{
    /// <summary>Takes new jobs when it says it can.</summary>
    Normal = 0,

    /// <summary>Takes no new jobs, connected or not.</summary>
    Paused = 1
}
