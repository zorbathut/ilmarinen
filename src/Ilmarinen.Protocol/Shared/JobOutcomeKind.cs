namespace Ilmarinen.Protocol;

public enum JobOutcomeKind
{
    /// <summary>The worker ran the job; its status says how it went.</summary>
    Completed,

    /// <summary>The worker was busy when the job arrived and handed it back unrun, to be queued again.</summary>
    Rejected
}
