using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using NUlid;
using System.Threading.Tasks;

namespace Ilmarinen.Server.Hubs;

public interface IJobLogsClient
{
    Task ReceiveLogChunk(LogBroadcast chunk);
    Task JobCompleted(Ulid jobId, JobStatus status);
}
