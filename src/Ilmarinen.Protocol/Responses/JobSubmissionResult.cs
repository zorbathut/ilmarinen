using System;

namespace Ilmarinen.Protocol.Responses;

/// <summary>
/// Server → Client: The job created by a submission, retry, or pipeline trigger.
/// </summary>
public record JobSubmissionResult
{
    public required Guid Id { get; init; }
}
