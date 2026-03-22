using NUlid;
using System;

namespace Ilmarinen.Protocol.Responses;

public record SubscriberInfo
{
    public required Ulid Id { get; init; }
    public required string Name { get; init; }
    public bool IsActive { get; init; }
    public DateTime? LastHeartbeat { get; init; }
}
