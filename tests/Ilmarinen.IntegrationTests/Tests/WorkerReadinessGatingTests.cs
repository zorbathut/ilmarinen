using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// The server's side of readiness: derived from what the worker last reported, and held back while a diagnostic the server asked for hasn't started.
/// Driven through the hub directly, as an arbitrarily old — or arbitrarily wedged — worker build would.
/// </summary>
[TestFixture]
[Category("Integration")]
public class WorkerReadinessGatingTests
{
    private const string ConnectionId = "conn-readiness";
    private IntegrationTestFixture _fixture = null!;
    private DirectedWorker _worker = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new IntegrationTestFixture();
        await _fixture.SetupAsync();

        _worker = await DirectedWorker.RegisterAsync(_fixture, "readiness-worker");
        await _worker.ConnectAsync(ConnectionId);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _fixture.DisposeAsync();
    }

    [Test]
    public async Task Resumed_IdleAndHealthy_IsReady()
    {
        await _worker.ResumeAsync(_worker.Idle(DiagnosticStatus.Healthy));

        Assert.That(await _worker.IsReadyAsync(), Is.True);
    }

    [Test]
    public async Task Resumed_Degraded_IsReady()
    {
        await _worker.ResumeAsync(_worker.Idle(DiagnosticStatus.Degraded));

        Assert.That(await _worker.IsReadyAsync(), Is.True, "degraded means the diagnostic failed to tidy up, not that the worker can't work");
    }

    [Test]
    public async Task Resumed_Unhealthy_IsNotReady()
    {
        await _worker.ResumeAsync(_worker.Idle(DiagnosticStatus.Unhealthy));

        Assert.That(await _worker.IsReadyAsync(), Is.False);
    }

    // A worker runs its first diagnostic as soon as it starts; until that has a verdict, nothing says it can work.
    [Test]
    public async Task Resumed_WithNoVerdictYet_IsNotReady()
    {
        await _worker.ResumeAsync(_worker.Diagnosing(previous: null));

        Assert.That(await _worker.IsReadyAsync(), Is.False);
    }

    [Test]
    public async Task Authenticated_ButNotResumed_IsNotReady()
    {
        Assert.That(await _worker.IsReadyAsync(), Is.False, "until Resume, the server hasn't reconciled with the worker");
    }

    [Test]
    public async Task StateReport_OnceTheDiagnosticRecovers_ReadiesTheWorker()
    {
        await _worker.ResumeAsync(_worker.Idle(DiagnosticStatus.Unhealthy));

        await _worker.ReportAsync(_worker.Diagnosing(DiagnosticStatus.Unhealthy));
        Assert.That(await _worker.IsReadyAsync(), Is.False, "still running");
        await _worker.ReportAsync(_worker.Idle(DiagnosticStatus.Healthy));

        Assert.That(await _worker.IsReadyAsync(), Is.True);
    }

    // A report overtaken by a later one mustn't undo it.
    [Test]
    public async Task StateReport_OlderThanTheLatest_IsIgnored()
    {
        await _worker.ResumeAsync(_worker.Idle(DiagnosticStatus.Healthy));
        var stale = _worker.Diagnosing();
        var latest = _worker.Idle(DiagnosticStatus.Healthy);

        await _worker.ReportAsync(latest);
        await _worker.ReportAsync(stale);

        Assert.That(await _worker.IsReadyAsync(), Is.True);
    }

    // The failure may be the host rather than the build, and the worker can't tell the difference — only a fresh
    // diagnostic can. Until one has started and come back healthy, this worker takes no more work.
    [Test]
    public async Task FailedOutcome_AsksForAFreshDiagnosticAndHoldsTheWorkerBackUntilItHasRun()
    {
        await _worker.ResumeAsync(_worker.Idle());
        var (jobId, assignmentId) = await StartJobAsync();

        await _worker.ReportAsync(DirectedWorker.Completed(jobId, assignmentId, JobStatus.Failed));
        await _worker.ReportAsync(_worker.Idle());

        Assert.That((await _worker.SentAsync()).DiagnosticRequests, Is.EqualTo(1));
        Assert.That(await _worker.IsReadyAsync(), Is.False, "idle and healthy, but the re-check hasn't run");

        await _worker.ReportAsync(_worker.Diagnosing());
        Assert.That(await _worker.IsReadyAsync(), Is.False, "the re-check is running");
        await _worker.ReportAsync(_worker.Idle());

        Assert.That(await _worker.IsReadyAsync(), Is.True);
    }

    // A reconnecting worker can deliver the same outcome twice (once live, once in its Resume). The first already asked for the re-check.
    [Test]
    public async Task FailedOutcome_DeliveredTwice_AsksForOneDiagnostic()
    {
        await _worker.ResumeAsync(_worker.Idle());
        var (jobId, assignmentId) = await StartJobAsync();
        var outcome = DirectedWorker.Completed(jobId, assignmentId, JobStatus.Failed);

        await _worker.ReportAsync(outcome);
        await _worker.ReportAsync(outcome);

        Assert.That((await _worker.SentAsync()).DiagnosticRequests, Is.EqualTo(1));
    }

    [Test]
    public async Task SuccessOutcome_ReadiesTheWorkerWithoutADiagnostic()
    {
        await _worker.ResumeAsync(_worker.Idle());
        var (jobId, assignmentId) = await StartJobAsync();

        await _worker.ReportAsync(DirectedWorker.Completed(jobId, assignmentId, JobStatus.Success));
        await _worker.ReportAsync(_worker.Idle());

        Assert.That(await _worker.IsReadyAsync(), Is.True);
        Assert.That((await _worker.SentAsync()).DiagnosticRequests, Is.EqualTo(0), "a job that worked is its own proof that the host works");
    }

    // A person cancelling a job says nothing about the host it was running on.
    [Test]
    public async Task CancelledOutcome_ReadiesTheWorkerWithoutADiagnostic()
    {
        await _worker.ResumeAsync(_worker.Idle());
        var (jobId, assignmentId) = await StartJobAsync();

        await _worker.ReportAsync(DirectedWorker.Completed(jobId, assignmentId, JobStatus.Cancelled));
        await _worker.ReportAsync(_worker.Idle());

        Assert.That(await _worker.IsReadyAsync(), Is.True);
        Assert.That((await _worker.SentAsync()).DiagnosticRequests, Is.EqualTo(0));
    }

    // The one piece of session state that must survive a server restart: without it, the restarted server would hand work to a worker whose host it was in the middle of re-checking.
    [Test]
    public async Task OwedDiagnostic_SurvivesAServerRestart()
    {
        await _worker.ResumeAsync(_worker.Idle());
        var (jobId, assignmentId) = await StartJobAsync();
        await _worker.ReportAsync(DirectedWorker.Completed(jobId, assignmentId, JobStatus.Failed));

        await _fixture.RestartServerAsync(workerBundlePath: null);
        await _worker.ConnectAsync("conn-after-restart");
        await _worker.ResumeAsync(_worker.Idle());

        Assert.That(await _worker.IsReadyAsync(), Is.False);
        Assert.That((await _worker.SentAsync()).DiagnosticRequests, Is.EqualTo(1), "the re-check is asked for again on the new connection");
    }

    [Test]
    public async Task OperatorDiagnostic_HoldsTheWorkerBackUntilItHasRun()
    {
        await _worker.ResumeAsync(_worker.Idle());

        Assert.That(await _fixture.Services.GetRequiredService<JobScheduler>().RequestDiagnosticAsync(_worker.WorkerId), Is.True);

        Assert.That((await _worker.SentAsync()).OperatorDiagnosticRequests, Is.EqualTo(1));
        Assert.That(await _worker.IsReadyAsync(), Is.False);
        await _worker.ReportAsync(_worker.Diagnosing());
        await _worker.ReportAsync(_worker.Idle());
        Assert.That(await _worker.IsReadyAsync(), Is.True);
    }

    private async Task<(Guid JobId, Guid AssignmentId)> StartJobAsync()
    {
        var jobId = await _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = "/nonexistent/repo",
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        // The worker is ready, so submitting already dispatched the job to it.
        var assignment = (await _worker.SentAsync()).Assignments.Find(a => a.Id == jobId) ?? throw new InvalidOperationException($"Job {jobId} wasn't dispatched to the worker");
        return (jobId, assignment.AssignmentId);
    }
}
