using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using Ilmarinen.Worker.Services;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.IntegrationTests.Fixtures;

/// <summary>
/// Reports a healthy worker without touching Docker, DNS or the registry. What test workers run unless a test asks for the real diagnostic, so that a registry hiccup or a slow daemon can't stop an unrelated test's worker from going Ready.
/// </summary>
public sealed class DiagnosticStub : IWorkerDiagnostic
{
    public Task<DiagnosticReport> RunAsync(string? workerContainerId, CancellationToken ct)
    {
        return Task.FromResult(new DiagnosticReport
        {
            Status = DiagnosticStatus.Healthy,
            Summary = "stub diagnostic",
            Steps = [],
            CheckedAt = DateTime.UtcNow
        });
    }
}
