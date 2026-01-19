using NUlid;

namespace Ilmarinen.Worker;

public class WorkerConfig
{
    public required string ServerUrl { get; init; }
    public Ulid WorkerId { get; init; } = Ulid.NewUlid();
    public string WorkspacePath { get; init; } = Path.Combine(Path.GetTempPath(), "ilmarinen-worker");


    /// <summary>
    /// Host-side path for Docker bind mounts. Auto-discovered at startup
    /// when running in Docker. Falls back to WorkspacePath when not containerized.
    /// </summary>
    public string HostWorkspacePath { get; set; } = "";

    public string GetHostWorkspacePath() =>
        string.IsNullOrEmpty(HostWorkspacePath) ? WorkspacePath : HostWorkspacePath;
}
