namespace Ilmarinen.Protocol;

/// <summary>What became of a request to punt a job.</summary>
public enum PuntStatus
{
    /// <summary>The worker is told to stop the run; the job goes back in the queue once it has.</summary>
    Requested = 0,

    /// <summary>This run was already being punted; nothing more was done.</summary>
    AlreadyRequested = 1,

    /// <summary>The job isn't running.</summary>
    NotRunning = 2,

    /// <summary>The run the request named has ended; the job is running again, or no longer.</summary>
    Stale = 3,

    /// <summary>It would come straight back to the same worker, and the request didn't say to punt anyway.</summary>
    WouldComeBack = 4
}
