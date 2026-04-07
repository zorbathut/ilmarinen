using System;
using Ilmarinen.Protocol.Responses;
using System;
using Ilmarinen.Protocol;
using System.Threading.Tasks;

namespace Ilmarinen.Server.Hubs;

public interface IJobLogsClient
{
    Task ReceiveLogChunk(LogBroadcast chunk);
    Task JobCompleted(Guid jobId, JobStatus status);
}
