using Ilmarinen.Protocol.Responses;
using System.Threading.Tasks;
using System.Threading;

namespace Ilmarinen.NotificationClient;

public interface INotificationHandler
{
    Task<bool> HandleAsync(JobNotification notification, CancellationToken ct);
}
