using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Hubs;
using Ilmarinen.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// The server's side of readiness: what it will and won't do with a worker whose own diagnostic says it is broken.
/// Driven through the hub directly, as an arbitrarily old — or arbitrarily wedged — worker build would.
/// </summary>
[TestFixture]
[Category("Integration")]
public class WorkerReadinessGatingTests
{
    private IntegrationTestFixture _fixture = null!;
    private WorkerRepository _workers = null!;
    private FakeHubCallerClients _clients = null!;
    private WorkerHub _hub = null!;
    private Guid _workerId;
    private const string ConnectionId = "conn-readiness";

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new IntegrationTestFixture();
        await _fixture.SetupAsync();

        _clients = new FakeHubCallerClients();
        (_hub, _workerId) = await _fixture.CreateHubForRegisteredWorkerAsync("readiness-worker", ConnectionId, _clients);
        _workers = _fixture.Services.GetRequiredService<WorkerRepository>();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _fixture.DisposeAsync();
    }

    [Test]
    public async Task Ready_FromWorkerWhoseDiagnosticFailed_IsIgnored()
    {
        _workers.SetDiagnostic(_workerId, Report(DiagnosticStatus.Unhealthy));

        await _hub.Ready();

        Assert.That(_workers.GetByConnectionId(ConnectionId)!.IsReady, Is.False);
    }

    [Test]
    public async Task Ready_OnceTheDiagnosticRecovers_IsAccepted()
    {
        _workers.SetDiagnostic(_workerId, Report(DiagnosticStatus.Unhealthy));
        await _hub.Ready();

        _workers.SetDiagnostic(_workerId, Report(DiagnosticStatus.Healthy));
        await _hub.Ready();

        Assert.That(_workers.GetByConnectionId(ConnectionId)!.IsReady, Is.True);
    }

    // The placeholder a worker pushes while a diagnostic is still running is not a verdict that it can work.
    [Test]
    public async Task Ready_WhileADiagnosticIsStillRunning_IsIgnored()
    {
        _workers.SetDiagnostic(_workerId, Report(DiagnosticStatus.Running));

        await _hub.Ready();

        Assert.That(_workers.GetByConnectionId(ConnectionId)!.IsReady, Is.False);
    }

    // A worker that has never reported one is not presumed broken: on every worker path the report precedes the Ready,
    // so an absent report means an old build, not a failed check.
    [Test]
    public async Task Ready_WithNoDiagnosticReported_IsAccepted()
    {
        await _hub.Ready();

        Assert.That(_workers.GetByConnectionId(ConnectionId)!.IsReady, Is.True);
    }

    [Test]
    public async Task JobCompleted_ByWorkerWhoseDiagnosticFailed_LeavesItOutOfTheFleet()
    {
        _workers.SetDiagnostic(_workerId, Report(DiagnosticStatus.Unhealthy));
        var jobId = await StartJobAsync();

        await _hub.JobCompleted(jobId, Result(jobId, JobStatus.Failed));

        Assert.That(_workers.GetByConnectionId(ConnectionId)!.IsReady, Is.False);
    }

    // The failure may be the host rather than the build, and the worker can't tell the difference — only a fresh
    // diagnostic can. Until it answers, this worker takes no more work.
    [Test]
    public async Task JobCompleted_Failed_AsksForAFreshDiagnosticInsteadOfTakingMoreWork()
    {
        _workers.SetDiagnostic(_workerId, Report(DiagnosticStatus.Healthy));
        var jobId = await StartJobAsync();

        await _hub.JobCompleted(jobId, Result(jobId, JobStatus.Failed));

        Assert.That(_workers.GetByConnectionId(ConnectionId)!.IsReady, Is.False, "a worker that just failed a job must not be handed the next one before its host is re-checked");
        Assert.That(_clients.CallerClient.DiagnosticRequests, Is.EqualTo(1));
    }

    [Test]
    public async Task JobCompleted_Success_ReadiesTheWorkerWithoutADiagnostic()
    {
        _workers.SetDiagnostic(_workerId, Report(DiagnosticStatus.Healthy));
        var jobId = await StartJobAsync();

        await _hub.JobCompleted(jobId, Result(jobId, JobStatus.Success));

        Assert.That(_workers.GetByConnectionId(ConnectionId)!.IsReady, Is.True);
        Assert.That(_clients.CallerClient.DiagnosticRequests, Is.EqualTo(0), "a job that worked is its own proof that the host works");
    }

    // A person cancelling a job says nothing about the host it was running on.
    [Test]
    public async Task JobCompleted_Cancelled_ReadiesTheWorkerWithoutADiagnostic()
    {
        _workers.SetDiagnostic(_workerId, Report(DiagnosticStatus.Healthy));
        var jobId = await StartJobAsync();

        await _hub.JobCompleted(jobId, Result(jobId, JobStatus.Cancelled));

        Assert.That(_workers.GetByConnectionId(ConnectionId)!.IsReady, Is.True);
        Assert.That(_clients.CallerClient.DiagnosticRequests, Is.EqualTo(0));
    }

    private async Task<Guid> StartJobAsync()
    {
        var jobId = await _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = "/nonexistent/repo",
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        await _fixture.StartJobOnWorkerAsync(jobId, _workerId);
        return jobId;
    }

    private static JobResult Result(Guid jobId, JobStatus status)
    {
        return new JobResult
        {
            Id = jobId,
            Status = status,
            Duration = TimeSpan.FromSeconds(1)
        };
    }

    private static DiagnosticReport Report(DiagnosticStatus status)
    {
        return new DiagnosticReport
        {
            Status = status,
            Summary = $"stub {status}",
            Steps = [],
            CheckedAt = DateTime.UtcNow
        };
    }
}
