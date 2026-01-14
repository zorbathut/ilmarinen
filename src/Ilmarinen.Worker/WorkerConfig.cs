using NUlid;

namespace Ilmarinen.Worker;

public class WorkerConfig
{
    public required string ServerUrl { get; init; }
    public Ulid WorkerId { get; init; } = Ulid.NewUlid();
    public string WorkspacePath { get; init; } = Path.Combine(Path.GetTempPath(), "ilmarinen-worker");
}
