using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Server.Hubs;

public interface IJobLogsClient
{
    Task ReceiveLogChunk(LogBroadcast chunk);
    Task JobCompleted(Guid jobId, JobStatus status);
}
