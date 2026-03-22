namespace Ilmarinen.Protocol.Requests;

public record SubscriberRegistration
{
    public required string Name { get; init; }
    public int HeartbeatTimeoutMinutes { get; init; } = 5;
}
