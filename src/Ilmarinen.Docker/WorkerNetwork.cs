using Docker.DotNet.Models;
using Docker.DotNet;
using System.Threading.Tasks;
using System.Threading;

namespace Ilmarinen.Docker;

internal static class WorkerNetwork
{
    /// <summary>
    /// Connects the worker's own container to a job's (or diagnostic's) network, so the job's containers can reach the AgentApiServer in it.
    /// </summary>
    /// <remarks>
    /// The default route follows the highest gateway priority, then network name, and job networks sort early: at the default priority a connection opened mid-job would leave from the job network and die silently when it's deleted. Below the default, it never wins the route over the worker's own network. Daemons older than Docker 28 ignore the priority (see <see cref="HonoursGatewayPriority"/>).
    /// </remarks>
    internal static async Task AttachAsync(DockerClient client, string network, string workerContainerId, CancellationToken ct)
    {
        await client.Networks.ConnectNetworkAsync(network, new NetworkConnectParameters
        {
            Container = workerContainerId,
            EndpointConfig = new EndpointSettings { GwPriority = -1 }
        }, ct);
    }

    /// <summary>
    /// Whether a daemon speaking this API version applies the gateway priority <see cref="AttachAsync"/> sets (API 1.48, Docker 28).
    /// </summary>
    internal static bool HonoursGatewayPriority(string apiVersion)
    {
        return System.Version.Parse(apiVersion) >= new System.Version(1, 48);
    }
}
