using System;

namespace Ilmarinen.Protocol.Requests;

/// <summary>
/// Worker → Server: what the worker is doing now, sent after every change. The server decides from it whether the worker can take a job.
/// </summary>
public record WorkerState
{
    /// <summary>Increases with every change, for the life of the worker process; the server keeps the highest it has seen, so a report overtaken by a later one can't undo it.</summary>
    public required long Seq { get; init; }

    public required WorkerActivity Activity { get; init; }

    /// <summary>The job being run, while <see cref="Activity"/> is RunningJob.</summary>
    public Guid? JobId { get; init; }

    public Guid? AssignmentId { get; init; }

    /// <summary>The verdict of the worker's latest diagnostic; null before its first one.</summary>
    public DiagnosticStatus? DiagnosticStatus { get; init; }

    /// <summary>Set while the worker takes no new jobs; it hands back any that cross this report on the way.</summary>
    public WorkerHold? Hold { get; init; }

    /// <summary>The persistent workspace the running job is using, once its script has named one; null otherwise. The worker refuses to delete it meanwhile.</summary>
    public string? Workspace { get; init; }
}
