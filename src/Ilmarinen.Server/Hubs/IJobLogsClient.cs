using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Responses;
using NUlid;

namespace Ilmarinen.Server.Hubs;

public interface IJobLogsClient
{
    Task ReceiveLogChunk(LogBroadcast chunk);
    Task JobCompleted(Ulid jobId, JobStatus status);
}
