using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Hubs;
using Ilmarinen.Server.Services;
using Ilmarinen.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// Covers the self-update drain machinery: a launcher-run worker (WorkerConfig.BundleHash set) learns the server's current bundle hash at auth time and stops itself with UpdateSignal set when stale — immediately when idle, after JobCompleted delivery when busy. Server restarts here reproduce the production update event (new process, new bundle, dropped connections).
/// </summary>
[TestFixture]
[Category("Integration")]
public class WorkerBundleDrainTests
{
    private IntegrationTestFixture _fixture = null!;

    [SetUp]
    public void SetUp()
    {
        _fixture = new IntegrationTestFixture();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _fixture.DisposeAsync();
    }

    private UpdateSignal WorkerUpdateSignal => _fixture.WorkerUpdateSignal!;

    private async Task WaitForWorkerStopAsync(int timeoutMs)
    {
        var stopped = await Task.WhenAny(_fixture.WorkerRunTask!, Task.Delay(timeoutMs)) == _fixture.WorkerRunTask;
        Assert.That(stopped, Is.True, $"Worker host did not stop within {timeoutMs}ms");
    }

    [Test]
    public async Task MatchingBundle_WorkerBecomesReady_AndReportsHash()
    {
        var (bundlePath, bundleHash) = await SetupWithBundleAsync();

        var workerId = await _fixture.StartWorkerAsync(new StubDiagnostic(), waitForReady: true, bundleHash: bundleHash);

        var workers = _fixture.Services.GetRequiredService<WorkerRepository>();
        var view = await workers.GetByIdAsync(workerId);
        Assert.That(view!.BundleHash, Is.EqualTo(bundleHash));
        Assert.That(WorkerUpdateSignal.UpdateRequired, Is.False);
    }

    [Test]
    public async Task ServerWithoutBundle_WorkerWithBundleHash_StaysReady()
    {
        await _fixture.SetupAsync();

        await _fixture.StartWorkerAsync(new StubDiagnostic(), waitForReady: true, bundleHash: "AAAA1111");

        // Null server hash means "no opinion", never "stale" — the worker must not drain.
        Assert.That(WorkerUpdateSignal.UpdateRequired, Is.False);
    }

    [Test]
    public async Task StaleBundle_IdleWorker_DrainsOnReconnect()
    {
        var (_, bundleHashA) = await SetupWithBundleAsync();
        var workerId = await _fixture.StartWorkerAsync(new StubDiagnostic(), waitForReady: true, bundleHash: bundleHashA);

        var (bundlePathB, bundleHashB) = await _fixture.CreateBundleFileAsync();
        Assert.That(bundleHashB, Is.Not.EqualTo(bundleHashA));
        await _fixture.RestartServerAsync(bundlePathB);

        // The worker auto-reconnects, learns the new hash at auth, and stops itself instead of going Ready.
        await WaitForWorkerStopAsync(timeoutMs: 90000);
        Assert.That(WorkerUpdateSignal.UpdateRequired, Is.True);

        var workers = _fixture.Services.GetRequiredService<WorkerRepository>();
        var view = await workers.GetByIdAsync(workerId);
        Assert.That(view!.IsReady, Is.False);
    }

    [Test]
    public async Task UnchangedBundle_AfterServerRestart_WorkerReadiesAgain()
    {
        var (bundlePathA, bundleHashA) = await SetupWithBundleAsync();
        var workerId = await _fixture.StartWorkerAsync(new StubDiagnostic(), waitForReady: true, bundleHash: bundleHashA);

        await _fixture.RestartServerAsync(bundlePathA);

        await _fixture.WaitForWorkerReadyAsync(workerId, timeoutMs: 90000);
        Assert.That(WorkerUpdateSignal.UpdateRequired, Is.False);
    }

    [Test]
    public async Task StaleBundle_BusyWorker_FinishesJobThenDrains_AndQueuedJobIsNotDispatched()
    {
        var (_, bundleHashA) = await SetupWithBundleAsync();
        await _fixture.StartWorkerAsync(new StubDiagnostic(), waitForReady: true, bundleHash: bundleHashA);

        using var repo = new TestGitRepository();
        repo.AddFile("pipeline.csx", """
            Step("long-running")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Shell("sleep 30");
                });
            """);
        repo.Commit("Add pipeline");

        var submission = new JobSubmission { RepoUrl = repo.Url, Ref = "master", ScriptPath = "pipeline.csx" };
        var jobId = await _fixture.SubmitJobAsync(submission);
        await _fixture.WaitForJobStatusAsync(jobId, JobStatus.Running, timeoutMs: 60000);

        // Second job queues behind the busy worker; it must remain queued through the drain.
        var queuedJobId = await _fixture.SubmitJobAsync(submission);

        var (bundlePathB, _) = await _fixture.CreateBundleFileAsync();
        await _fixture.RestartServerAsync(bundlePathB);

        // The worker reconnects mid-job, keeps running it, completes it, then exits instead of accepting more work.
        var job = await _fixture.WaitForJobCompletionAsync(jobId, timeoutMs: 120000);
        Assert.That(job.Status, Is.EqualTo(JobStatus.Success));

        await WaitForWorkerStopAsync(timeoutMs: 90000);
        Assert.That(WorkerUpdateSignal.UpdateRequired, Is.True);

        var queuedJob = await _fixture.GetJobAsync(queuedJobId);
        Assert.That(queuedJob.Status, Is.EqualTo(JobStatus.Queued));
    }

    [Test]
    public async Task PersistentHandshakeFailure_LauncherMode_ExitsForUpdate()
    {
        var (bundlePathA, bundleHashA) = await SetupWithBundleAsync();
        var workerId = await _fixture.StartWorkerAsync(new StubDiagnostic(), waitForReady: true, bundleHash: bundleHashA);

        // Revoke the worker, then restart the server so the next handshake actually runs — and fails with "Unknown worker" every time. This stands in for any persistent handshake failure, protocol breaks included: the worker can't tell them apart, which is the point of the fallback.
        using (var scope = _fixture.Services.CreateScope())
        {
            var registration = scope.ServiceProvider.GetRequiredService<WorkerRegistrationService>();
            await registration.RevokeWorkerAsync(workerId);
        }
        await _fixture.RestartServerAsync(bundlePathA);

        // Three consecutive failures accrue at heartbeat cadence (~65s for the third), so this test is inherently slow.
        await WaitForWorkerStopAsync(timeoutMs: 150000);
        Assert.That(WorkerUpdateSignal.UpdateRequired, Is.True);
    }

    [Test]
    public async Task PersistentHandshakeFailure_ClassicMode_KeepsRetrying()
    {
        var (bundlePathA, _) = await SetupWithBundleAsync();
        var workerId = await _fixture.StartWorkerAsync(new StubDiagnostic(), waitForReady: true, bundleHash: null);

        using (var scope = _fixture.Services.CreateScope())
        {
            var registration = scope.ServiceProvider.GetRequiredService<WorkerRegistrationService>();
            await registration.RevokeWorkerAsync(workerId);
        }
        await _fixture.RestartServerAsync(bundlePathA);

        // Long enough for at least three failed handshake cycles — the threshold that makes a launcher-mode worker exit.
        await Task.Delay(TimeSpan.FromSeconds(80));
        Assert.That(WorkerUpdateSignal.UpdateRequired, Is.False);
        Assert.That(_fixture.WorkerRunTask!.IsCompleted, Is.False, "Classic worker should retry forever, not stop");
    }

    [Test]
    public async Task Ready_FromStaleBundleConnection_IsIgnoredServerSide()
    {
        // The server must not trust a stale worker's own gating — this invokes the hub directly, as an arbitrarily old worker build might.
        var (_, bundleHashA) = await SetupWithBundleAsync();

        using var scope = _fixture.Services.CreateScope();
        var registration = scope.ServiceProvider.GetRequiredService<WorkerRegistrationService>();
        var result = await registration.RegisterWorkerAsync("stale-ready-worker");

        var workers = _fixture.Services.GetRequiredService<WorkerRepository>();
        const string connectionId = "conn-stale-ready";
        await workers.ConnectAsync(connectionId, result.WorkerId, ipAddress: null);
        workers.SetBundleHash(connectionId, "0000STALE0000");

        var hub = new WorkerHub(
            _fixture.Services.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            _fixture.Services.GetRequiredService<ServerKeyService>(),
            _fixture.Services.GetRequiredService<WorkerBundleService>(),
            _fixture.Services.GetRequiredService<UIEventService>(),
            _fixture.Services.GetRequiredService<ILogger<WorkerHub>>())
        {
            Context = new FakeHubCallerContext(connectionId)
        };

        await hub.Ready();
        Assert.That(workers.GetByConnectionId(connectionId)!.IsReady, Is.False);

        // Same connection with the current bundle is accepted
        workers.SetBundleHash(connectionId, bundleHashA);
        await hub.Ready();
        Assert.That(workers.GetByConnectionId(connectionId)!.IsReady, Is.True);
    }

    private async Task<(string Path, string Hash)> SetupWithBundleAsync()
    {
        // The bundle file must exist before setup: the server hashes it at startup
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ilmarinen-test-bundle-{Guid.NewGuid():N}.zip");
        var content = Guid.NewGuid().ToByteArray();
        await System.IO.File.WriteAllBytesAsync(path, content);
        await _fixture.SetupAsync(workerBundlePath: path);
        _fixture.TrackBundleFile(path);
        return (path, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)));
    }

    private class StubDiagnostic : IWorkerDiagnostic
    {
        public Task<DiagnosticReport> RunAsync(string? workerContainerId, CancellationToken ct)
        {
            return Task.FromResult(new DiagnosticReport
            {
                Status = DiagnosticStatus.Healthy,
                Summary = "stub",
                Steps = [],
                CheckedAt = DateTime.UtcNow
            });
        }
    }
}
