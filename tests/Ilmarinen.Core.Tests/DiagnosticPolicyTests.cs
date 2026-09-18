using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using Ilmarinen.Worker.Services;
using NUnit.Framework;
using System.Linq;
using System;

namespace Ilmarinen.Core.Tests;

[TestFixture]
public class DiagnosticPolicyTests
{
    [TestCase(DiagnosticStatus.Healthy, false)]
    [TestCase(DiagnosticStatus.Degraded, false, Description = "a degraded worker still takes jobs, so there is nothing to recover from")]
    [TestCase(DiagnosticStatus.Unhealthy, true)]
    [TestCase(DiagnosticStatus.Running, true, Description = "a worker left showing Running takes no jobs either")]
    public void ShouldRetry_ByReportedStatus(DiagnosticStatus status, bool expected)
    {
        Assert.That(DiagnosticPolicy.ShouldRetry(Report(status), connected: true, draining: false), Is.EqualTo(expected));
    }

    [Test]
    public void ShouldRetry_NothingReportedYet_IsNo()
    {
        Assert.That(DiagnosticPolicy.ShouldRetry(null, connected: true, draining: false), Is.False);
    }

    [Test]
    public void ShouldRetry_Disconnected_IsNo()
    {
        Assert.That(DiagnosticPolicy.ShouldRetry(Report(DiagnosticStatus.Unhealthy), connected: false, draining: false), Is.False, "there would be nowhere to report the answer");
    }

    [Test]
    public void ShouldRetry_Draining_IsNo()
    {
        Assert.That(DiagnosticPolicy.ShouldRetry(Report(DiagnosticStatus.Unhealthy), connected: true, draining: true), Is.False, "a draining worker is about to exit and must not start containers");
    }

    [Test]
    public void RetryDelay_FirstAttempt_IsSecondsNotMinutes()
    {
        Assert.That(DiagnosticPolicy.RetryDelay(1), Is.LessThan(TimeSpan.FromMinutes(1)));
    }

    [Test]
    public void RetryDelay_KeepsFailing_BacksOffToACeiling()
    {
        var delays = Enumerable.Range(1, 12).Select(DiagnosticPolicy.RetryDelay).ToList();

        Assert.That(delays[1], Is.GreaterThan(delays[0]), "a host that keeps failing should be retried less often");
        Assert.That(delays.Last(), Is.EqualTo(delays[^2]), "the wait should settle at a ceiling rather than growing without bound");
        Assert.That(delays.Last(), Is.LessThan(TimeSpan.FromHours(1)), "the ceiling is a pause, not an abandonment");
    }

    [Test]
    public void RetryDelay_WorkerFailingForWeeks_StaysAtTheCeiling()
    {
        // The delay doubles per failure, so this is where an unbounded exponent would throw instead of settling.
        Assert.That(DiagnosticPolicy.RetryDelay(int.MaxValue), Is.EqualTo(DiagnosticPolicy.RetryDelay(12)));
    }

    [Test]
    public void Jitter_SpreadsAroundTheDelayWithoutRunningAway()
    {
        var delay = TimeSpan.FromSeconds(100);

        var jittered = Enumerable.Range(0, 50).Select(_ => DiagnosticPolicy.Jitter(delay)).ToList();

        Assert.That(jittered, Is.All.InRange(delay * 0.8, delay * 1.2));
        Assert.That(jittered.Distinct().Count(), Is.GreaterThan(1), "a fleet that failed together must not retry in lockstep");
    }

    private static DiagnosticReport Report(DiagnosticStatus status)
    {
        return new DiagnosticReport
        {
            Status = status,
            Summary = "test",
            Steps = [],
            CheckedAt = DateTime.UtcNow
        };
    }
}
