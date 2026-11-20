using System;

namespace Ilmarinen.Protocol.Responses;

/// <summary>Server → Client: one entry of a list of priorities (GET /api/priorities/{kind}, highest first).</summary>
public record PriorityInfo
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }

    /// <summary>What a new worker, or a job given no priority, gets.</summary>
    public bool IsDefault { get; init; }

    public required PriorityUsage Usage { get; init; }
}
