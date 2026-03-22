using System;

namespace Ilmarinen.NotificationClient;

public class NotificationProcessorOptions
{
    public required string SubscriberName { get; set; }
    public int HeartbeatTimeoutMinutes { get; set; } = 5;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(60);
    public int BatchSize { get; set; } = 10;
}
