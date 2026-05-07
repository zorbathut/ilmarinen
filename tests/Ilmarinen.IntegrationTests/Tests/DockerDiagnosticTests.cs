using Ilmarinen.Docker;
using Ilmarinen.Protocol;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
[Category("Integration")]
public class DockerDiagnosticTests
{
    [Test]
    public async Task Diagnostic_OnHealthyHost_ReturnsHealthy()
    {
        await using var diagnostic = new DockerDiagnostic(workerContainerId: null);

        var report = await diagnostic.RunAsync(progress: null, ct: CancellationToken.None);

        Assert.That(report.Status, Is.EqualTo(DiagnosticStatus.Healthy),
            $"Expected Healthy, got {report.Status}. Summary: {report.Summary}. " +
            $"Steps: {string.Join("; ", report.Steps.Select(s => $"{s.Name}={s.Success}"))}");
        Assert.That(report.Steps, Has.Count.EqualTo(7));
        Assert.That(report.Steps.Select(s => s.Name), Is.EqualTo(new[]
        {
            "docker_daemon", "image_pull", "container_run",
            "output_capture", "container_internet", "agent_api_reachability", "cleanup"
        }));
        Assert.That(report.Steps.All(s => s.Success), Is.True,
            $"Expected all steps successful. Failures: {string.Join("; ", report.Steps.Where(s => !s.Success).Select(s => $"{s.Name}: {s.Message}"))}");
        Assert.That(report.Summary, Is.EqualTo("All checks passed."));
    }

    [Test]
    public async Task Diagnostic_DaemonUnreachable_ReturnsUnhealthy()
    {
        // Point at a TCP port nothing is listening on. Using the internal ctor avoids
        // mutating DOCKER_HOST, which would race with parallel tests in this assembly.
        await using var diagnostic = new DockerDiagnostic(
            dockerHostUri: "tcp://127.0.0.1:1", workerContainerId: null);

        var report = await diagnostic.RunAsync(progress: null, ct: CancellationToken.None);

        Assert.That(report.Status, Is.EqualTo(DiagnosticStatus.Unhealthy));
        // After the fatal docker_daemon failure, only cleanup runs (which succeeds because no resources were allocated).
        Assert.That(report.Steps, Has.Count.EqualTo(2));
        Assert.That(report.Steps[0].Name, Is.EqualTo("docker_daemon"));
        Assert.That(report.Steps[0].Success, Is.False);
        Assert.That(report.Steps[0].Failure,
            Is.AnyOf(FailureKind.SocketUnreachable, FailureKind.DaemonError));
        Assert.That(report.Steps[1].Name, Is.EqualTo("cleanup"));
        Assert.That(report.Summary, Does.StartWith("docker_daemon failed:"));
    }

    [Test]
    public async Task Diagnostic_StreamsProgress_ToReporter()
    {
        await using var diagnostic = new DockerDiagnostic(workerContainerId: null);
        var observed = new List<string>();
        var progress = new Progress<DiagnosticStepResult>(r =>
        {
            // Note: Progress<T> reports on the synchronization context — for tests we
            // don't need ordering guarantees with the result list, only that we get
            // each step exactly once.
            lock (observed) observed.Add(r.Name);
        });

        var report = await diagnostic.RunAsync(progress, CancellationToken.None);

        // Wait briefly for any pending progress callbacks to fire.
        await Task.Delay(100);

        Assert.That(report.Status, Is.EqualTo(DiagnosticStatus.Healthy));
        lock (observed)
        {
            Assert.That(observed, Has.Count.EqualTo(report.Steps.Count));
            Assert.That(observed, Is.EquivalentTo(report.Steps.Select(s => s.Name)));
        }
    }
}
