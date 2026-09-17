using Ilmarinen.Docker;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Threading;
using System;

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
    private readonly ILogger<DockerWorkerDiagnostic> _logger;

    public DockerWorkerDiagnostic(SleepInhibitor sleepInhibitor, ILogger<DockerWorkerDiagnostic> logger)
    {
        _sleepInhibitor = sleepInhibitor;
        _logger = logger;
    }

    public async Task<DiagnosticReport> RunAsync(string? workerContainerId, CancellationToken ct)
    {
        // Handed to DockerDiagnostic as an extra step rather than run alongside it, so it inherits the step harness: timing, and the catch that turns a throwing step into a failed step instead of letting it escape and mark the whole worker Unhealthy over an optional capability.
        DockerDiagnostic.DiagnosticStep[] extraSteps =
        [
            new DockerDiagnostic.DiagnosticStep("host_sleep_inhibit", DiagnosticStepKind.Advisory, _sleepInhibitor.ProbeAsync),
        ];

        await using var diag = new DockerDiagnostic(workerContainerId, extraSteps);
        return await diag.RunAsync(new StepLogger(_logger), ct);
    }

    /// <summary>
    /// Logs each step as it finishes, so a diagnostic that never finishes still shows the last step it got through. Reports synchronously, unlike Progress&lt;T&gt;, which posts to the thread pool and would log steps out of order and late.
    /// </summary>
    private sealed class StepLogger : IProgress<DiagnosticStepResult>
    {
        private readonly ILogger _logger;

        public StepLogger(ILogger logger)
        {
            _logger = logger;
        }

        public void Report(DiagnosticStepResult step)
        {
            _logger.LogInformation("Diagnostic step {Step} ({Kind}) {Outcome} in {DurationMs}ms: {Message}", step.Name, step.Kind, step.Success ? "passed" : "failed", (long)step.Duration.TotalMilliseconds, step.Message);
        }
    }
}
