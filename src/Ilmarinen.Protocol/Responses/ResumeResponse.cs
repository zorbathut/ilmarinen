using System.Collections.Generic;
using System;

namespace Ilmarinen.Protocol.Responses;

/// <summary>
/// Server → Worker: the answer to Resume. On Ok, every outcome the Resume carried has been applied.
/// </summary>
public record ResumeResponse
{
    public required SessionAnswer Answer { get; init; }

    /// <summary>Assignments the worker reported running that the server no longer has running for it — cancelled or failed while the worker was away. The worker stops them.</summary>
    public IReadOnlyList<Guid> AbortAssignments { get; init; } = [];
}
