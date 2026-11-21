using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
[Category("Integration")]
public class WorkerConnectionTests
{
    private IntegrationTestFixture _fixture = null!;
    private TestGitRepository _repo = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new IntegrationTestFixture();
        await _fixture.SetupAsync();

        _repo = new TestGitRepository();
        _repo.AddFile("pipeline.csx", """
            Step("quick")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Exec("echo", "done");
                });
            """);
        _repo.Commit("Add pipeline");
    }

    [TearDown]
    public async Task TearDown()
    {
        _repo.Dispose();
        await _fixture.DisposeAsync();
    }

    [Test]
    public async Task Worker_WhenStarted_RegistersWithServer()
    {
        // Act
        var workerId = await _fixture.StartWorkerAsync();

        // Assert - Worker registration succeeded (would have thrown if not)
        Assert.That(workerId, Is.Not.EqualTo(Guid.Empty));
    }

    [Test]
    public async Task Worker_WhenConnected_RecordsLastIpAddress()
    {
        var workerId = await _fixture.StartWorkerAsync(diagnostic: null, waitForReady: false);

        using var scope = _fixture.Services.CreateScope();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();
        var view = await workers.GetByIdAsync(workerId);

        Assert.That(view, Is.Not.Null);
        Assert.That(view!.LastIpAddress, Is.Not.Null, "worker's connecting IP should have been recorded");

        // The fixture binds localhost on both stacks, so the peer is 127.0.0.1 or ::1 depending on which the client picked — assert the property, not the literal.
        var address = IPAddress.Parse(view.LastIpAddress!);
        Assert.That(IPAddress.IsLoopback(address), Is.True, $"expected a loopback address, got {view.LastIpAddress}");
    }

    [Test]
    public async Task Worker_WhenDisconnected_RetainsLastIpAddress()
    {
        var workerId = await _fixture.StartWorkerAsync(diagnostic: null, waitForReady: false);

        string? connectedIp;
        using (var scope = _fixture.Services.CreateScope())
        {
            var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();
            connectedIp = (await workers.GetByIdAsync(workerId))!.LastIpAddress;
        }
        Assert.That(connectedIp, Is.Not.Null);

        await _fixture.StopWorkerAsync();

        using var afterScope = _fixture.Services.CreateScope();
        var afterWorkers = afterScope.ServiceProvider.GetRequiredService<WorkerRepository>();
        var view = await afterWorkers.GetByIdAsync(workerId);

        Assert.That(view!.IsConnected, Is.False);
        Assert.That(view.LastIpAddress, Is.EqualTo(connectedIp), "last IP must survive the worker going away — that's exactly when an operator needs it");
    }

    // Kept while the worker is away: a worker that is asleep still has to be counted before the fleet can be said to have moved past a version.
    [Test]
    public async Task Worker_RecordsItsProtocolVersion_AndKeepsItWhileAway()
    {
        var workerId = await _fixture.StartWorkerAsync(diagnostic: null, waitForReady: false);

        var workers = _fixture.Services.GetRequiredService<WorkerRepository>();
        var connected = await workers.GetByIdAsync(workerId);
        Assert.That(connected!.ProtocolVersion, Is.EqualTo(WorkerProtocol.Version));
        Assert.That(connected.ProtocolBehind, Is.False);

        await _fixture.StopWorkerAsync();

        var away = await workers.GetByIdAsync(workerId);
        Assert.That(away!.IsConnected, Is.False);
        Assert.That(away.ProtocolVersion, Is.EqualTo(WorkerProtocol.Version));
    }

    [Test]
    public void ProtocolBehind_IsAVersionBelowTheServersOwn()
    {
        WorkerView View(int? version)
        {
            return new WorkerView { Id = Guid.NewGuid(), Name = "w", Priority = new PriorityRef(Guid.NewGuid(), "p"), ProtocolVersion = version };
        }

        Assert.Multiple(() =>
        {
            Assert.That(View(WorkerProtocol.Version - 1).ProtocolBehind, Is.True);
            Assert.That(View(WorkerProtocol.Version).ProtocolBehind, Is.False);
            Assert.That(View(null).ProtocolBehind, Is.False, "never connected with a version: nothing to say");
        });
    }

    [Test]
    public async Task Worker_WhenConnectionHasNoAddress_KeepsPreviousLastIpAddress()
    {
        using var scope = _fixture.Services.CreateScope();
        var registration = scope.ServiceProvider.GetRequiredService<WorkerRegistrationService>();
        var result = await registration.RegisterWorkerAsync($"addressless-{Guid.NewGuid():N}");

        var workers = _fixture.Services.GetRequiredService<WorkerRepository>();
        await workers.RecordConnectionAsync(result.WorkerId, "10.0.0.5", WorkerProtocol.Version);
        await workers.RecordConnectionAsync(result.WorkerId, ipAddress: null, WorkerProtocol.Version);

        var view = await workers.GetByIdAsync(result.WorkerId);
        Assert.That(view!.LastIpAddress, Is.EqualTo("10.0.0.5"), "an address-less connection must not blank out a known address");
    }

    [Test]
    public async Task Job_WhenWorkerNotAvailable_StaysQueued()
    {
        // Arrange - No worker started
        var submission = new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        };

        // Act
        var jobId = await _fixture.SubmitJobAsync(submission);
        await Task.Delay(1000); // Give it time to potentially (incorrectly) run

        // Assert
        var job = await _fixture.GetJobAsync(jobId);
        Assert.That(job.Status, Is.EqualTo(JobStatus.Queued));
    }

    [Test]
    public async Task Job_WhenWorkerStartsLater_GetsExecuted()
    {
        // Arrange - Submit job first (no worker yet)
        var submission = new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        };
        var jobId = await _fixture.SubmitJobAsync(submission);

        // Verify queued
        var queuedJob = await _fixture.GetJobAsync(jobId);
        Assert.That(queuedJob.Status, Is.EqualTo(JobStatus.Queued));

        // Act - Start worker. Not waiting for Ready: the queued job is dispatched the moment the worker reports it, so Ready may never be observable.
        await _fixture.StartWorkerAsync(diagnostic: null, waitForReady: false);

        // Assert - Job should complete
        var completedJob = await _fixture.WaitForJobCompletionAsync(jobId);
        Assert.That(completedJob.Status, Is.EqualTo(JobStatus.Success));
    }

    // A worker whose connection dropped without the server noticing reauthenticates on a new one while the old is still
    // half-open. Dispatch used to be able to pick either, and a job handed to the dead one is never delivered and never
    // reconciled — the worker is already connected, so nothing ever reconnects to correct it.
    [Test]
    public async Task Worker_Reauthenticating_LeavesOnlyItsNewestConnectionDispatchable()
    {
        var worker = await DirectedWorker.RegisterAsync(_fixture, "reconnecting-worker");
        await worker.ConnectAsync("conn-stale");
        await worker.ResumeAsync(worker.Idle());
        var staleHub = worker.Hub;

        await worker.ConnectAsync("conn-live");
        await worker.ResumeAsync(worker.Idle());

        var workers = _fixture.Services.GetRequiredService<WorkerRepository>();
        Assert.That(_fixture.Services.GetRequiredService<WorkerSessions>().ByConnection("conn-stale"), Is.Null, "the superseded connection must not still resolve to the worker");
        Assert.That(workers.FindConnectionIdByWorkerId(worker.WorkerId), Is.EqualTo("conn-live"));
        Assert.That(await staleHub.ReportState(worker.Idle()), Is.EqualTo(SessionAnswer.Fenced), "and anything it says is refused");

        var candidates = await workers.GetReadyWorkersByPriorityAsync();
        Assert.That(candidates.Select(c => c.WorkerId), Is.EqualTo(new[] { worker.WorkerId }));
    }

    // The evicted connection's disconnect arrives later and must not unmap the connection that replaced it.
    [Test]
    public async Task Worker_StaleConnectionDisconnecting_DoesNotUnmapTheLiveOne()
    {
        var worker = await DirectedWorker.RegisterAsync(_fixture, "late-disconnect-worker");
        await worker.ConnectAsync("conn-stale");
        var staleHub = worker.Hub;
        await worker.ConnectAsync("conn-live");

        await staleHub.OnDisconnectedAsync(null);

        Assert.That(_fixture.Services.GetRequiredService<WorkerRepository>().FindConnectionIdByWorkerId(worker.WorkerId), Is.EqualTo("conn-live"));
    }
}
