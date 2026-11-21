using System.Collections.Generic;
using System;

namespace Ilmarinen.Protocol.Responses;

/// <summary>Server → Client: whether dispatch is held for a drain, and the jobs still running, which a deploy should wait for.</summary>
public record DrainStatus
{
    public required bool Draining { get; init; }
    public required IReadOnlyList<DrainRunningJob> Running { get; init; }
}

/// <summary>A running job and the worker running it; the name is null if the worker has since been revoked.</summary>
public record DrainRunningJob(Guid JobId, string? WorkerName);
