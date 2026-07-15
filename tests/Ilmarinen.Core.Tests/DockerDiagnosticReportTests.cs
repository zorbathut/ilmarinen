using Ilmarinen.Docker;
using Ilmarinen.Protocol;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.Core.Tests;

/// <summary>
/// The diagnostic's capability checks decide whether a worker can run jobs; its cleanup step only decides
/// whether it tidied up after itself, and its advisory steps decide nothing at all. Conflating any of them is what
/// let a transient teardown error quarantine a fully functional worker, so these pin the derivation directly.
/// </summary>
[TestFixture]
public class DockerDiagnosticReportTests
{
    private static readonly DateTime CheckedAt = new(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc);

    private static DiagnosticStepResult Step(string name, bool success, DiagnosticStepKind kind)
    {
        return new DiagnosticStepResult
        {
            Name = name,
            Success = success,
            Kind = kind,
            Message = success ? "fine" : $"{name} blew up"
        };
    }

    private static DiagnosticStepResult Capability(string name, bool success)
    {
        return Step(name, success, DiagnosticStepKind.Capability);
    }

    private static DiagnosticStepResult Cleanup(bool success)
    {
        return Step("cleanup", success, DiagnosticStepKind.Hygiene);
    }

    private static DiagnosticStepResult Advisory(string name, bool success)
    {
        return Step(name, success, DiagnosticStepKind.Advisory);
    }

    [Test]
    public void AllStepsPass_IsHealthy()
    {
        var report = DockerDiagnostic.BuildReport(
            [Capability("docker_daemon", true), Capability("image_pull", true), Cleanup(true)],
            CheckedAt);

        Assert.That(report.Status, Is.EqualTo(DiagnosticStatus.Healthy));
        Assert.That(report.Summary, Is.EqualTo("All checks passed."));
    }

    // The regression: every capability check passed, so the worker can run jobs. A failed teardown must not
    // make it refuse work — that quarantines a healthy worker permanently and doesn't clean the leak either.
    [Test]
    public void CleanupFails_ButCapabilitiesPass_IsDegradedNotUnhealthy()
    {
        var report = DockerDiagnostic.BuildReport(
            [Capability("docker_daemon", true), Capability("agent_api_reachability", true), Cleanup(false)],
            CheckedAt);

        Assert.That(report.Status, Is.EqualTo(DiagnosticStatus.Degraded));
        Assert.That(report.Summary, Does.StartWith("All checks passed, but cleanup failed:"));
        Assert.That(report.Summary, Does.Contain("cleanup blew up"));
    }

    // An advisory step reports a capability the worker doesn't need — host sleep inhibition, say, which most hosts
    // can't do and don't miss. It must not colour the worker's health at all, or every headless worker in the fleet
    // sits permanently Degraded for lacking a nicety.
    [Test]
    public void AdvisoryFails_IsStillHealthy()
    {
        var report = DockerDiagnostic.BuildReport(
            [Capability("docker_daemon", true), Cleanup(true), Advisory("host_sleep_inhibit", false)],
            CheckedAt);

        Assert.That(report.Status, Is.EqualTo(DiagnosticStatus.Healthy));
        Assert.That(report.Summary, Is.EqualTo("All checks passed."));
    }

    [Test]
    public void FailedAdvisoryStep_StaysVisibleInTheReport()
    {
        var report = DockerDiagnostic.BuildReport(
            [Capability("docker_daemon", true), Advisory("host_sleep_inhibit", false)],
            CheckedAt);

        var advisory = report.Steps.Single(s => s.Name == "host_sleep_inhibit");
        Assert.That(advisory.Success, Is.False);
        Assert.That(advisory.Message, Is.EqualTo("host_sleep_inhibit blew up"));
    }

    // A genuinely broken worker must still be Unhealthy even when an advisory step is also failing alongside it.
    [Test]
    public void CapabilityFailsAlongsideAdvisory_IsUnhealthy()
    {
        var report = DockerDiagnostic.BuildReport(
            [Capability("docker_daemon", false), Advisory("host_sleep_inhibit", false)],
            CheckedAt);

        Assert.That(report.Status, Is.EqualTo(DiagnosticStatus.Unhealthy));
        Assert.That(report.Summary, Does.StartWith("docker_daemon failed:"));
    }

    [Test]
    public void CapabilityFails_IsUnhealthy()
    {
        var report = DockerDiagnostic.BuildReport(
            [Capability("docker_daemon", false), Cleanup(true)],
            CheckedAt);

        Assert.That(report.Status, Is.EqualTo(DiagnosticStatus.Unhealthy));
        Assert.That(report.Summary, Does.StartWith("docker_daemon failed:"));
    }

    // A broken worker whose cleanup also failed is still just broken: the capability failure is the actionable
    // one, so it must own the summary rather than being masked by the teardown noise that follows it.
    [Test]
    public void CapabilityAndCleanupBothFail_ReportsTheCapabilityFailure()
    {
        var report = DockerDiagnostic.BuildReport(
            [Capability("docker_daemon", false), Cleanup(false)],
            CheckedAt);

        Assert.That(report.Status, Is.EqualTo(DiagnosticStatus.Unhealthy));
        Assert.That(report.Summary, Does.StartWith("docker_daemon failed:"));
        Assert.That(report.Summary, Does.Not.Contain("cleanup"));
    }

    [Test]
    public void FailedCleanupStep_StaysVisibleInTheReport()
    {
        var report = DockerDiagnostic.BuildReport(
            [Capability("docker_daemon", true), Cleanup(false)],
            CheckedAt);

        var cleanup = report.Steps.Single(s => s.Name == "cleanup");
        Assert.That(cleanup.Success, Is.False);
        Assert.That(cleanup.Message, Is.EqualTo("cleanup blew up"));
    }

    [Test]
    public void StepOrderAndCheckedAt_ArePreserved()
    {
        var report = DockerDiagnostic.BuildReport(
            [Capability("docker_daemon", true), Capability("image_pull", true), Cleanup(true)],
            CheckedAt);

        Assert.That(report.Steps.Select(s => s.Name), Is.EqualTo(new[] { "docker_daemon", "image_pull", "cleanup" }));
        Assert.That(report.CheckedAt, Is.EqualTo(CheckedAt));
    }

    private static DockerDiagnostic.DiagnosticStep Runs(string name, DiagnosticStepKind kind, bool success, List<string> ran)
    {
        return new DockerDiagnostic.DiagnosticStep(name, kind, _ =>
        {
            ran.Add(name);
            return Task.FromResult(new DiagnosticStepResult { Name = name, Success = success, Message = $"{name} ran" });
        });
    }

    [Test]
    public async Task CapabilityFailure_SkipsLaterCapabilityChecks_ButStillRunsCleanupAndAdvisory()
    {
        var ran = new List<string>();

        var executed = await DockerDiagnostic.ExecuteStepsAsync(
            [
                Runs("docker_daemon", DiagnosticStepKind.Capability, false, ran),
                Runs("image_pull", DiagnosticStepKind.Capability, true, ran),
                Runs("cleanup", DiagnosticStepKind.Hygiene, true, ran),
                Runs("host_sleep_inhibit", DiagnosticStepKind.Advisory, true, ran),
            ],
            progress: null,
            CancellationToken.None);

        Assert.That(ran, Is.EqualTo(new[] { "docker_daemon", "cleanup", "host_sleep_inhibit" }));
        Assert.That(DockerDiagnostic.BuildReport(executed, CheckedAt).Status, Is.EqualTo(DiagnosticStatus.Unhealthy));
    }

    // Guards the inverse of the bug: the abort rule keys off *capability* failures, so a step of any other kind
    // that fails must not silently skip the capability checks that follow it.
    [Test]
    public async Task NonCapabilityFailure_DoesNotAbortLaterCapabilityChecks()
    {
        var ran = new List<string>();

        var executed = await DockerDiagnostic.ExecuteStepsAsync(
            [
                Runs("housekeeping", DiagnosticStepKind.Hygiene, false, ran),
                Runs("docker_daemon", DiagnosticStepKind.Capability, true, ran),
                Runs("image_pull", DiagnosticStepKind.Capability, true, ran),
            ],
            progress: null,
            CancellationToken.None);

        Assert.That(ran, Is.EqualTo(new[] { "housekeeping", "docker_daemon", "image_pull" }));
        Assert.That(DockerDiagnostic.BuildReport(executed, CheckedAt).Status, Is.EqualTo(DiagnosticStatus.Degraded));
    }

    // The step's Kind is what the whole health derivation reads, so the harness must stamp it onto the result —
    // a step delegate that forgets to set it would otherwise silently default to Capability and be able to
    // quarantine the worker.
    [Test]
    public async Task ExecuteSteps_StampsTheKindOntoTheResult()
    {
        var ran = new List<string>();

        var executed = await DockerDiagnostic.ExecuteStepsAsync(
            [Runs("host_sleep_inhibit", DiagnosticStepKind.Advisory, false, ran)],
            progress: null,
            CancellationToken.None);

        Assert.That(executed.Single().Kind, Is.EqualTo(DiagnosticStepKind.Advisory));
        Assert.That(DockerDiagnostic.BuildReport(executed, CheckedAt).Status, Is.EqualTo(DiagnosticStatus.Healthy));
    }

    [Test]
    public async Task StepThatThrows_BecomesAFailedResultRatherThanEscaping()
    {
        var executed = await DockerDiagnostic.ExecuteStepsAsync(
            [new DockerDiagnostic.DiagnosticStep("docker_daemon", DiagnosticStepKind.Capability, _ => throw new InvalidOperationException("socket gone"))],
            progress: null,
            CancellationToken.None);

        Assert.That(executed.Single().Success, Is.False);
        Assert.That(executed.Single().Message, Does.Contain("socket gone"));
    }

    // An advisory step that throws must degrade to a failed advisory step, not escape and mark the worker Unhealthy.
    // This is the whole reason the step is handed to the harness rather than run alongside it.
    [Test]
    public async Task AdvisoryStepThatThrows_DoesNotAffectHealth()
    {
        var executed = await DockerDiagnostic.ExecuteStepsAsync(
            [
                new DockerDiagnostic.DiagnosticStep("docker_daemon", DiagnosticStepKind.Capability, _ => Task.FromResult(new DiagnosticStepResult { Name = "docker_daemon", Success = true })),
                new DockerDiagnostic.DiagnosticStep("host_sleep_inhibit", DiagnosticStepKind.Advisory, _ => throw new InvalidOperationException("dbus exploded")),
            ],
            progress: null,
            CancellationToken.None);

        Assert.That(DockerDiagnostic.BuildReport(executed, CheckedAt).Status, Is.EqualTo(DiagnosticStatus.Healthy));
        Assert.That(executed.Single(s => s.Name == "host_sleep_inhibit").Message, Does.Contain("dbus exploded"));
    }
}
