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

    public Ulid WorkerId { get; init; } = Ulid.NewUlid();
    public string WorkspacePath { get; init; } = Path.Combine(Path.GetTempPath(), "ilmarinen-worker");


    /// <summary>
    /// Host-side path for Docker bind mounts. Auto-discovered at startup
    /// when running in Docker. Falls back to WorkspacePath when not containerized.
    /// </summary>
    public string HostWorkspacePath { get; set; } = "";

    public string GetHostWorkspacePath() =>
        string.IsNullOrEmpty(HostWorkspacePath) ? WorkspacePath : HostWorkspacePath;

    public string GetPublicApiUrl()
    {
        if (!string.IsNullOrEmpty(PublicApiUrl))
            return PublicApiUrl;

        // Default: replace port 8081 with 8080
        return ServerUrl.Replace(":8081", ":8080");
    }
}
