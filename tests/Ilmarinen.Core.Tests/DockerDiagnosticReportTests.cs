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
/// whether it tidied up after itself. Conflating the two is what let a transient teardown error quarantine
/// a fully functional worker, so these pin the derivation directly.
/// </summary>
[TestFixture]
public class DockerDiagnosticReportTests
{
    private static readonly DateTime CheckedAt = new(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc);

    private static DockerDiagnostic.DiagnosticStepOutcome Step(string name, bool success, bool isFatal)
    {
        return new DockerDiagnostic.DiagnosticStepOutcome(
            new DiagnosticStepResult
            {
                Name = name,
                Success = success,
                Message = success ? "fine" : $"{name} blew up"
            },
            isFatal);
    }

    private static DockerDiagnostic.DiagnosticStepOutcome Capability(string name, bool success)
    {
        return Step(name, success, isFatal: true);
    }

    private static DockerDiagnostic.DiagnosticStepOutcome Cleanup(bool success)
    {
        return Step("cleanup", success, isFatal: false);
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

    private static DockerDiagnostic.DiagnosticStep Runs(string name, bool isFatal, bool success, List<string> ran)
    {
        return new DockerDiagnostic.DiagnosticStep(name, isFatal, _ =>
        {
            ran.Add(name);
            return Task.FromResult(new DiagnosticStepResult { Name = name, Success = success, Message = $"{name} ran" });
        });
    }

    [Test]
    public async Task FatalFailure_SkipsLaterCapabilityChecks_ButStillRunsCleanup()
    {
        var ran = new List<string>();

        var executed = await DockerDiagnostic.ExecuteStepsAsync(
            [Runs("docker_daemon", true, false, ran), Runs("image_pull", true, true, ran), Runs("cleanup", false, true, ran)],
            progress: null,
            CancellationToken.None);

        Assert.That(ran, Is.EqualTo(new[] { "docker_daemon", "cleanup" }));
        Assert.That(DockerDiagnostic.BuildReport(executed, CheckedAt).Status, Is.EqualTo(DiagnosticStatus.Unhealthy));
    }

    // Guards the inverse of the bug: the abort rule keys off *fatal* failures, so a non-fatal step that fails
    // must not silently skip the capability checks that follow it.
    [Test]
    public async Task NonFatalFailure_DoesNotAbortLaterCapabilityChecks()
    {
        var ran = new List<string>();

        var executed = await DockerDiagnostic.ExecuteStepsAsync(
            [Runs("housekeeping", false, false, ran), Runs("docker_daemon", true, true, ran), Runs("image_pull", true, true, ran)],
            progress: null,
            CancellationToken.None);

        Assert.That(ran, Is.EqualTo(new[] { "housekeeping", "docker_daemon", "image_pull" }));
        Assert.That(DockerDiagnostic.BuildReport(executed, CheckedAt).Status, Is.EqualTo(DiagnosticStatus.Degraded));
    }

    [Test]
    public async Task StepThatThrows_BecomesAFailedResultRatherThanEscaping()
    {
        var executed = await DockerDiagnostic.ExecuteStepsAsync(
            [new DockerDiagnostic.DiagnosticStep("docker_daemon", true, _ => throw new InvalidOperationException("socket gone"))],
            progress: null,
            CancellationToken.None);

        Assert.That(executed.Single().Result.Success, Is.False);
        Assert.That(executed.Single().Result.Message, Does.Contain("socket gone"));
    }
}
