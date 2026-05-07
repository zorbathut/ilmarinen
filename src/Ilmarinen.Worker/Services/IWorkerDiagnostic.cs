using Ilmarinen.Docker;
using Ilmarinen.Protocol.Requests;
using System.Threading.Tasks;
using System.Threading;

namespace Ilmarinen.Worker.Services;

/// <summary>
/// Runs a self-check that exercises the capabilities a worker needs to actually run jobs.
/// Abstracted so tests can substitute a stub without spinning up real Docker.
/// </summary>
public interface IWorkerDiagnostic
{
    Task<DiagnosticReport> RunAsync(string? workerContainerId, CancellationToken ct);
}

public class DockerWorkerDiagnostic : IWorkerDiagnostic
{
    public async Task<DiagnosticReport> RunAsync(string? workerContainerId, CancellationToken ct)
    {
        await using var diag = new DockerDiagnostic(workerContainerId);
        return await diag.RunAsync(progress: null, ct);
    }
}
