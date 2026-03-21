using System.Security.Cryptography;
using NUlid;

namespace Ilmarinen.Worker;

public class WorkerConfig
{
    /// <summary>
    /// SignalR hub URL (worker port, typically 8081).
    /// </summary>
    public required string ServerUrl { get; init; }

    /// <summary>
    /// REST API URL (public port, typically 8080) for artifact uploads.
    /// Defaults to deriving from ServerUrl by replacing port 8081 with 8080.
    /// </summary>
    public string? PublicApiUrl { get; init; }

    /// <summary>
    /// Combined worker key: {name}:{ulidBase64}:{workerPrivBase64}:{serverPubBase64}
    /// Set via ILMARINEN_WORKER_KEY environment variable.
    /// The name is for human readability; the ULID is the actual identity.
    /// </summary>
    public required string WorkerKey { get; init; }
    public string WorkspacePath { get; init; } = Path.Combine(Path.GetTempPath(), "ilmarinen-worker");


    /// <summary>
    /// Host-side path for Docker bind mounts. Auto-discovered at startup
    /// when running in Docker. Falls back to WorkspacePath when not containerized.
    /// </summary>
    public string HostWorkspacePath { get; set; } = "";

    /// <summary>
    /// Container ID of the worker when running in Docker-in-Docker.
    /// Used to connect the worker to job networks for AgentApiServer communication.
    /// Null when running outside of Docker.
    /// </summary>
    public string? WorkerContainerId { get; set; }

    public string GetHostWorkspacePath() =>
        string.IsNullOrEmpty(HostWorkspacePath) ? WorkspacePath : HostWorkspacePath;

    public string GetPublicApiUrl()
    {
        if (!string.IsNullOrEmpty(PublicApiUrl))
            return PublicApiUrl;

        // Default: replace port 8081 with 8080
        return ServerUrl.Replace(":8081", ":8080");
    }

    public Ulid GetWorkerId()
    {
        var parts = GetKeyParts();
        return Ulid.Parse(parts.id);
    }

    public ECDsa GetWorkerPrivateKey()
    {
        var parts = GetKeyParts();
        var privateScalar = Convert.FromBase64String(parts.workerPriv);
        return ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = privateScalar
        });
    }

    public ECDsa GetServerPublicKey()
    {
        var parts = GetKeyParts();
        var publicKeyBytes = Convert.FromBase64String(parts.serverPub);
        var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);
        return ecdsa;
    }

    private (string name, string id, string workerPriv, string serverPub) GetKeyParts()
    {
        var parts = WorkerKey.Split(':');
        if (parts.Length != 4)
            throw new InvalidOperationException(
                "ILMARINEN_WORKER_KEY is malformed. Expected format: {name}:{id}:{workerPrivBase64}:{serverPubBase64}");

        return (parts[0], parts[1], parts[2], parts[3]);
    }
}
