using System;

namespace Ilmarinen.Protocol.Responses;

/// <summary>
/// Server → Worker: Assign job via SignalR Client.AssignJob()
/// </summary>
public record JobAssignment
{
    public required Guid Id { get; init; }

    /// <summary>Names this one handing of the job to a worker, so its outcome can't be confused with that of an earlier or later one.</summary>
    public required Guid AssignmentId { get; init; }
    public required string RepoUrl { get; init; }
    public required string Ref { get; init; }
    public required string ScriptPath { get; init; }
    public string? GitToken { get; init; }

    /// <summary>The persistent workspace the job is expected to use, from its script's latest runs; null if it is expected to use none, or nothing is known.</summary>
    public string? PredictedWorkspace { get; init; }

    /// <summary>Free space the job is expected to take, beyond the floor the worker keeps: before starting, the worker deletes its least recently used workspaces, never <see cref="PredictedWorkspace"/>, until it has that much plus its floor free.</summary>
    public long DiskNeededBytes { get; init; }

    /// <summary>Delete this worker's copy of <see cref="PredictedWorkspace"/> before starting: the server found the job has room only made afresh. The worker checks first that reusing it, needing <see cref="DiskNeededToReuseBytes"/>, doesn't fit after all.</summary>
    public bool StartFresh { get; init; }

    /// <summary>Free space the job is expected to take reusing <see cref="PredictedWorkspace"/>; set with <see cref="StartFresh"/>.</summary>
    public long DiskNeededToReuseBytes { get; init; }
}
