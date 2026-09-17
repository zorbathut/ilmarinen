using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Server.Services;
using Ilmarinen.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
[Category("Integration")]
public class WorkerDiagnosticGatingTests
{
    private IntegrationTestFixture _fixture = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new IntegrationTestFixture();
        await _fixture.SetupAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _fixture.DisposeAsync();
    }

    [Test]
    public async Task UnhealthyDiagnostic_DoesNotMakeWorkerReady()
    {
        var stub = new StubDiagnostic(DiagnosticStatus.Unhealthy, "synthetic failure for test");

        var workerId = await _fixture.StartWorkerAsync(_ => stub, waitForReady: false);

        // Wait for the diagnostic to be reported (which is what flips the status to Unhealthy on the server)
        await WaitForDiagnosticAsync(workerId, expected: DiagnosticStatus.Unhealthy);

        using var scope = _fixture.Services.CreateScope();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();
        var view = await workers.GetByIdAsync(workerId);

        Assert.That(view, Is.Not.Null);
        Assert.That(view!.IsConnected, Is.True, "worker should be connected");
        Assert.That(view.IsReady, Is.False, "worker must not be Ready when diagnostic is Unhealthy");
        Assert.That(view.Diagnostic, Is.Not.Null);
        Assert.That(view.Diagnostic!.Status, Is.EqualTo(DiagnosticStatus.Unhealthy));
        Assert.That(view.Diagnostic.Summary, Does.Contain("synthetic failure for test"));
    }

    [Test]
    public async Task HealthyDiagnostic_MakesWorkerReady()
    {
        var stub = new StubDiagnostic(DiagnosticStatus.Healthy, "stub healthy");

        var workerId = await _fixture.StartWorkerAsync(_ => stub, waitForReady: true);

        using var scope = _fixture.Services.CreateScope();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();
        var view = await workers.GetByIdAsync(workerId);

        Assert.That(view, Is.Not.Null);
        Assert.That(view!.IsReady, Is.True);
        Assert.That(view.Diagnostic?.Status, Is.EqualTo(DiagnosticStatus.Healthy));
        Assert.That(stub.CallCount, Is.EqualTo(1));
    }

    // A worker whose capability checks all passed but whose teardown failed can still run jobs, so it must
    // take work rather than quarantine itself out of the fleet over leftover scratch resources.
    [Test]
    public async Task DegradedDiagnostic_MakesWorkerReady()
    {
        var stub = new StubDiagnostic(DiagnosticStatus.Degraded, "All checks passed, but cleanup failed: stub");

        var workerId = await _fixture.StartWorkerAsync(_ => stub, waitForReady: true);

        using var scope = _fixture.Services.CreateScope();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();
        var view = await workers.GetByIdAsync(workerId);

        Assert.That(view, Is.Not.Null);
        Assert.That(view!.IsReady, Is.True, "a degraded worker is still functional and must accept jobs");
        Assert.That(view.Diagnostic?.Status, Is.EqualTo(DiagnosticStatus.Degraded));
    }

    // Host sleep inhibition is a nice-to-have that most hosts can't do at all. The real diagnostic must report it
    // without letting it colour the worker's health — a worker that quarantined itself for lacking an optional
    // capability would take the whole fleet out on any host with no D-Bus.
    [Test]
    public async Task SleepInhibitionUnavailable_IsReportedButWorkerStillTakesWork()
    {
        Assume.That(DnsProbe.CanResolveRegistry(), "the real diagnostic needs a resolver that can reach the registry");

        // The real diagnostic, whose SleepInhibitor points at a dead bus, so this is the unavailable branch, deterministically.
        var workerId = await _fixture.StartWorkerAsync(TestWorkerBuilder.RealDiagnostic, waitForReady: true);

        using var scope = _fixture.Services.CreateScope();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();
        var view = await workers.GetByIdAsync(workerId);

        Assert.That(view, Is.Not.Null);
        Assert.That(view!.IsReady, Is.True, "a worker that cannot inhibit host sleep is still perfectly able to run jobs");
        Assert.That(view.Diagnostic?.Status, Is.EqualTo(DiagnosticStatus.Healthy));

        var step = view.Diagnostic!.Steps.SingleOrDefault(s => s.Name == "host_sleep_inhibit");
        Assert.That(step, Is.Not.Null, "the capability should be reported even when absent");
        Assert.That(step!.Kind, Is.EqualTo(DiagnosticStepKind.Advisory));
        Assert.That(step.Success, Is.False);
        Assert.That(step.Suggestion, Is.Not.Null.And.Not.Empty, "an absent capability should say how to get it");

        // Cleanup tears down the scratch container, network and agent API that the other steps build, so it has to stay last — an injected step that ran after it would be looking at a torn-down harness.
        Assert.That(view.Diagnostic.Steps.Last().Name, Is.EqualTo("cleanup"));
    }

    [Test]
    public async Task DiagnosticReRunOnWorkerRestart()
    {
        var stub = new StubDiagnostic(DiagnosticStatus.Healthy, "stub healthy");

        await _fixture.StartWorkerAsync(_ => stub, waitForReady: true);
        Assert.That(stub.CallCount, Is.EqualTo(1));

        await _fixture.StopWorkerAsync(preserveIdentity: true);
        await _fixture.RestartWorkerAsync();

        Assert.That(stub.CallCount, Is.EqualTo(2), "a fresh worker process has no cached diagnostic, so it must run one before it can be Ready");
    }

    [Test]
    public async Task DiagnosticNotReRunOnReconnect()
    {
        var stub = new StubDiagnostic(DiagnosticStatus.Healthy, "stub healthy");

        var workerId = await _fixture.StartWorkerAsync(_ => stub, waitForReady: true);
        Assert.That(stub.CallCount, Is.EqualTo(1));

        // The restarted server starts with no in-memory connection or Ready state, so Ready can only come from this same worker process having reconnected.
        await _fixture.RestartServerAsync(null);
        await _fixture.WaitForWorkerReadyAsync(workerId, timeoutMs: 90000);

        Assert.That(stub.CallCount, Is.EqualTo(1), "a reconnecting worker must replay its cached diagnostic, not run a new one");
    }

    private async Task WaitForDiagnosticAsync(Guid workerId, DiagnosticStatus expected, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            using var scope = _fixture.Services.CreateScope();
            var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();
            var view = await workers.GetByIdAsync(workerId);
            if (view?.Diagnostic?.Status == expected)
                return;
            await Task.Delay(100);
        }
        throw new TimeoutException(
            $"Worker {workerId} diagnostic did not reach {expected} within {timeoutMs}ms");
    }

    private class StubDiagnostic : IWorkerDiagnostic
    {
        private readonly DiagnosticStatus _status;
        private readonly string _summary;
        private int _callCount;

        public StubDiagnostic(DiagnosticStatus status, string summary)
        {
            _status = status;
            _summary = summary;
        }

        public int CallCount => _callCount;

        public Task<DiagnosticReport> RunAsync(string? workerContainerId, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new DiagnosticReport
            {
                Status = _status,
                Summary = _summary,
                Steps = [],
                CheckedAt = DateTime.UtcNow
            });
        }
    }
}
