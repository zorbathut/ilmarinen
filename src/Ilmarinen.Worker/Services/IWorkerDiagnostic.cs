using Ilmarinen.Docker;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
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
    private readonly SleepInhibitor _sleepInhibitor;

    public DockerWorkerDiagnostic(SleepInhibitor sleepInhibitor)
    {
        _sleepInhibitor = sleepInhibitor;
    }

    public async Task<DiagnosticReport> RunAsync(string? workerContainerId, CancellationToken ct)
    {
        // Handed to DockerDiagnostic as an extra step rather than run alongside it, so it inherits the step harness: timing, and the catch that turns a throwing step into a failed step instead of letting it escape and mark the whole worker Unhealthy over an optional capability.
        DockerDiagnostic.DiagnosticStep[] extraSteps =
        [
            new DockerDiagnostic.DiagnosticStep("host_sleep_inhibit", DiagnosticStepKind.Advisory, _sleepInhibitor.ProbeAsync),
        ];

        await using var diag = new DockerDiagnostic(workerContainerId, extraSteps);
        return await diag.RunAsync(progress: null, ct);
    }
}
