using Ilmarinen.Protocol.Responses;

namespace Ilmarinen.Server.Hubs;

public interface IWorkerClient
{
    Task AssignJob(JobAssignment assignment);
    Task CancelJob(string jobId);
    Task Ping();
    Task DeleteWorkspace(string workspaceName);
}
