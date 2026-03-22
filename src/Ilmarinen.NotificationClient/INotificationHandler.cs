using Ilmarinen.Protocol.Responses;

namespace Ilmarinen.NotificationClient;

public interface INotificationHandler
{
    Task<bool> HandleAsync(JobNotification notification, CancellationToken ct);
}
