namespace Ilmarinen.Protocol;

/// <summary>Why a worker is taking no new jobs, whatever it is doing now: a job it is running carries on. When several hold at once the worker reports the last listed, the one that will last longest.</summary>
public enum WorkerHold
{
    /// <summary>The worker process is stopping.</summary>
    ShuttingDown
}
