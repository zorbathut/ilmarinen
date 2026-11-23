namespace Ilmarinen.Protocol;

/// <summary>Why a worker is taking no new jobs, whatever it is doing now: a job it is running carries on. When several hold at once the worker reports the last listed, the one that will last longest. Only <see cref="InUse"/> can be overridden, by an assignment that says to take it anyway (<see cref="Responses.JobAssignment.TakeWhileInUse"/>).</summary>
public enum WorkerHold
{
    /// <summary>A person is using the worker's machine. Advisory: the server may send a job anyway, saying to take it.</summary>
    InUse,

    /// <summary>The worker's machine is going to sleep.</summary>
    Sleeping,

    /// <summary>The worker process is stopping.</summary>
    ShuttingDown
}
