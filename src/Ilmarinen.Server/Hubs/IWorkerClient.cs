using Ilmarinen.Protocol.Responses;
using System.Threading.Tasks;

namespace Ilmarinen.Server.Hubs;

public interface IWorkerClient
{
    Task AssignJob(JobAssignment assignment);
    Task CancelJob(string jobId);
    Task Ping();
    Task DeleteWorkspace(string workspaceName);
}
