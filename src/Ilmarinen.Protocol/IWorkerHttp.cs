using Ilmarinen.Protocol.Responses;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Protocol;

/// <summary>
/// What a worker calls on the server over plain HTTP, on the worker port beside the hub: the calls whose bodies a SignalR message can't carry. Nothing implements this; it declares those endpoints so the worker protocol's shape covers them (<see cref="WorkerProtocol"/>), which makes every endpoint under /hub/workers/ part of the worker protocol. Each method's parameters are the request's route and query values, its result the response body.
/// </summary>
public interface IWorkerHttp
{
    /// <summary><c>POST /hub/workers/jobs/{jobId}/artifacts?name={name}</c>, with the artifact's bytes as the body.</summary>
    Task<ArtifactInfo> UploadArtifact(Guid jobId, string name);
}
