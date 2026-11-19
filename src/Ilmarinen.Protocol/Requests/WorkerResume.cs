using System.Collections.Generic;

namespace Ilmarinen.Protocol.Requests;

/// <summary>
/// Worker → Server: the first call on a connection once it has authenticated. Carries everything the server needs to reconcile with the worker, taken together so nothing the worker does can fall between them.
/// </summary>
public record WorkerResume
{
    public required WorkerState State { get; init; }

    /// <summary>Every outcome the server hasn't acknowledged yet.</summary>
    public required IReadOnlyList<JobOutcome> Outcomes { get; init; }
}
