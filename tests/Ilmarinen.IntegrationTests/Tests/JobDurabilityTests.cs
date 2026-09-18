using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
[Category("Integration")]
public class JobDurabilityTests
{
    private IntegrationTestFixture _fixture = null!;
    private TestGitRepository _repo = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new IntegrationTestFixture();
        await _fixture.SetupAsync();

        _repo = new TestGitRepository();
    }

    [TearDown]
    public async Task TearDown()
    {
        _repo.Dispose();
        await _fixture.DisposeAsync();
    }

    // A worker that is shutting down can't finish what it is running, and it still has a connection to say so. Left to
    // reconciliation the job sits Running until the worker comes back — and never resolves at all if it doesn't.
    // (A connection that merely drops is a different case: the job keeps running and completes, which
    // StaleBundle_BusyWorker_FinishesJobThenDrains_AndQueuedJobIsNotDispatched covers by restarting the server mid-job.)
    [Test]
    public async Task Worker_Shutdown_MidJob_FailsTheJobItCannotFinish()
    {
        _repo.AddFile("pipeline.csx", """
            Step("slow")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Exec("touch", "/workspace/step-started");
                    await ctx.Exec("sleep", "60");
                });
            """);
        _repo.Commit("Add slow pipeline");

        await _fixture.StartWorkerAsync();

        var jobId = await _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        await _fixture.WaitForJobStatusAsync(jobId, JobStatus.Running);
        await WaitForStepToStartAsync(jobId);

        await _fixture.StopWorkerAsync(preserveIdentity: true);

        var job = await _fixture.GetJobAsync(jobId);
        Assert.That(job.Status, Is.EqualTo(JobStatus.Failed),
            "a worker shutting down should report the job it killed, not leave it Running for a reconnect that may never come");
    }

    /// <summary>
    /// Waits until the step container is genuinely up and running, by watching for the marker the pipeline above makes
    /// inside the workspace it shares with the host. Job status only says the worker accepted the job, which it does
    /// well before there is a container to tear down.
    /// </summary>
    private async Task WaitForStepToStartAsync(Guid jobId, int timeoutMs = 60000)
    {
        var marker = Path.Combine(_fixture.WorkerWorkspacePath!, jobId.ToString(), "step-started");
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(marker))
            {
                return;
            }
            await Task.Delay(200);
        }
        throw new TimeoutException($"Job {jobId} never started its step within {timeoutMs}ms (no {marker})");
    }

    [Test]
    public async Task Worker_Restart_OrphanFailed_ThenNewJobSucceeds()
    {
        _repo.AddFile("pipeline.csx", """
            Step("quick")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Exec("echo", "hello");
                });
            """);
        _repo.AddFile("slow.csx", """
            Step("slow")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Exec("sleep", "30");
                });
            """);
        _repo.Commit("Add pipelines");

        await _fixture.StartWorkerAsync();

        // Start a long-running job
        var orphanedJobId = await _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "slow.csx"
        });

        await _fixture.WaitForJobStatusAsync(orphanedJobId, JobStatus.Running);

        // Restart worker — the running job is lost
        await _fixture.StopWorkerAsync(preserveIdentity: true);
        await _fixture.RestartWorkerAsync();

        // Orphaned job should be failed
        var orphanedJob = await _fixture.WaitForJobCompletionAsync(orphanedJobId, timeoutMs: 30000);
        Assert.That(orphanedJob.Status, Is.EqualTo(JobStatus.Failed));

        // Worker should still be functional — submit a new job
        var newJobId = await _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        var newJob = await _fixture.WaitForJobCompletionAsync(newJobId);
        Assert.That(newJob.Status, Is.EqualTo(JobStatus.Success),
            "Worker should be ready for new jobs after reconnecting");
    }

    // The backstop for a worker that died without reporting: it comes back not knowing about the job, and the server has
    // to decide the job is lost rather than leave it Running forever. Driven through the hub, because a worker that
    // stops gracefully now reports the job itself and never reaches this path.
    [Test]
    public async Task Reconnect_WithoutTheJobTheServerExpects_FailsIt()
    {
        var (hub, workerId) = await _fixture.CreateHubForRegisteredWorkerAsync("orphan-worker", "conn-orphan", new FakeHubCallerClients());
        var jobId = await SubmitAsync();
        await _fixture.StartJobOnWorkerAsync(jobId, workerId);

        var response = await hub.Reconnect(new WorkerReconnect { RunningJobId = null });

        Assert.That(response.ExpectedJobId, Is.EqualTo(jobId), "the server should say what it still expects, so a worker that does hold the job keeps it");
        Assert.That((await _fixture.GetJobAsync(jobId)).Status, Is.EqualTo(JobStatus.Failed));
    }

    // The same reconnect from a worker that *is* still running the job must leave it alone.
    [Test]
    public async Task Reconnect_StillHoldingTheJob_LeavesItRunning()
    {
        var (hub, workerId) = await _fixture.CreateHubForRegisteredWorkerAsync("holding-worker", "conn-holding", new FakeHubCallerClients());
        var jobId = await SubmitAsync();
        await _fixture.StartJobOnWorkerAsync(jobId, workerId);

        await hub.Reconnect(new WorkerReconnect { RunningJobId = jobId });

        Assert.That((await _fixture.GetJobAsync(jobId)).Status, Is.EqualTo(JobStatus.Running));
    }

    private async Task<Guid> SubmitAsync()
    {
        return await _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });
    }

    /// <summary>A job left Running by a worker that is registered but not connected — what a killed or partitioned worker leaves behind.</summary>
    private async Task<Guid> StartJobOnUnreachableWorkerAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var registration = scope.ServiceProvider.GetRequiredService<WorkerRegistrationService>();
        var workerId = (await registration.RegisterWorkerAsync("unreachable-worker")).WorkerId;

        var jobId = await SubmitAsync();
        await _fixture.StartJobOnWorkerAsync(jobId, workerId);
        return jobId;
    }

    // A worker that vanished without a word — killed, powered off, partitioned — leaves its job Running with nobody to
    // report it. The operator must not have to wait for that worker to come back to get rid of the job.
    [Test]
    public async Task Cancel_WhileWorkerUnreachable_TakesEffectImmediately()
    {
        var jobId = await StartJobOnUnreachableWorkerAsync();

        var cancelled = await _fixture.CancelJobAsync(jobId);
        Assert.That(cancelled, Is.True);

        var job = await _fixture.GetJobAsync(jobId);
        Assert.That(job.Status, Is.EqualTo(JobStatus.Cancelled));
    }

    [Test]
    public async Task Cancel_WhileWorkerUnreachable_NotifiesSubscribers()
    {
        var subscriber = await _fixture.RegisterSubscriberAsync(
            new SubscriberRegistration { Name = "cancel-signal-test" });

        var jobId = await StartJobOnUnreachableWorkerAsync();

        var cancelled = await _fixture.CancelJobAsync(jobId);
        Assert.That(cancelled, Is.True);

        // No worker restart: the cancel itself must produce the subscriber notification, because no worker will ever report this job.
        var notifications = await _fixture.PullNotificationsAsync(subscriber.Id);
        Assert.That(notifications.Select(n => n.JobId), Does.Contain(jobId),
            "cancelling must notify subscribers even when no worker will ever report the job");
    }

    [Test]
    public async Task Worker_Restart_AfterJobCancelledWhileDown_TakesNewWork()
    {
        _repo.AddFile("pipeline.csx", """
            Step("slow")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Exec("sleep", "30");
                });
            """);
        _repo.AddFile("quick.csx", """
            Step("quick")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Exec("echo", "hello");
                });
            """);
        _repo.Commit("Add pipelines");

        await _fixture.StartWorkerAsync();

        var jobId = await _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        // Wait until the job is running
        await _fixture.WaitForJobStatusAsync(jobId, JobStatus.Running);

        // Disconnect the worker, cancel the job, then reconnect
        await _fixture.StopWorkerAsync(preserveIdentity: true);

        await _fixture.CancelJobAsync(jobId);

        // A restarted worker is a fresh process with no job to abort; the server tells it so, and what matters is that it goes back to work.
        await _fixture.RestartWorkerAsync();

        // Worker should become ready and able to take new jobs
        var newJobId = await _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "quick.csx"
        });

        var newJob = await _fixture.WaitForJobCompletionAsync(newJobId);
        Assert.That(newJob.Status, Is.EqualTo(JobStatus.Success),
            "a worker whose job was cancelled while it was down should take new work");
    }

    /// <summary>
    /// If a job was cancelled while the worker was out of touch, but the worker had actually already completed it, the real outcome wins. Drives the status transition through the repository rather than a real worker: what's under test is the transition rule, not the reporting path.
    /// </summary>
    [Test]
    public async Task CancelledJob_CanBeOverriddenByRealCompletion()
    {
        _repo.AddFile("pipeline.csx", """
            Step("quick")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Exec("echo", "done");
                });
            """);
        _repo.Commit("Add pipeline");

        await _fixture.StartWorkerAsync();

        var jobId = await _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        await _fixture.WaitForJobStatusAsync(jobId, JobStatus.Running);

        // The cancel a worker out of touch would never have heard about
        await _fixture.CancelJobAsync(jobId);
        var cancelled = await _fixture.GetJobAsync(jobId);
        Assert.That(cancelled.Status, Is.EqualTo(JobStatus.Cancelled));

        // The completion that worker had already earned, arriving late
        using var scope = _fixture.Services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<Ilmarinen.Server.Services.JobRepository>();
        await jobs.UpdateStatusAsync(jobId, JobStatus.Success);

        var job = await _fixture.GetJobAsync(jobId);
        Assert.That(job.Status, Is.EqualTo(JobStatus.Success),
            "A real Success should override Cancelled — the work actually happened");
    }

    [Test]
    public async Task CancelledJob_CanBeOverriddenByRealFailure()
    {
        _repo.AddFile("pipeline.csx", """
            Step("quick")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Exec("echo", "done");
                });
            """);
        _repo.Commit("Add pipeline");

        await _fixture.StartWorkerAsync();

        var jobId = await _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        await _fixture.WaitForJobStatusAsync(jobId, JobStatus.Running);

        await _fixture.CancelJobAsync(jobId);

        // Real failure overrides cancel
        using var scope = _fixture.Services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<Ilmarinen.Server.Services.JobRepository>();
        await jobs.UpdateStatusAsync(jobId, JobStatus.Failed);

        var job = await _fixture.GetJobAsync(jobId);
        Assert.That(job.Status, Is.EqualTo(JobStatus.Failed),
            "A real Failed should override Cancelled — the job actually ran and errored");
    }

    [Test]
    public async Task SuccessJob_CannotBeOverridden()
    {
        _repo.AddFile("pipeline.csx", """
            Step("quick")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Exec("echo", "done");
                });
            """);
        _repo.Commit("Add pipeline");

        await _fixture.StartWorkerAsync();

        var jobId = await _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        var completed = await _fixture.WaitForJobCompletionAsync(jobId);
        Assert.That(completed.Status, Is.EqualTo(JobStatus.Success));

        // Nothing should override Success
        using var scope = _fixture.Services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<Ilmarinen.Server.Services.JobRepository>();
        await jobs.UpdateStatusAsync(jobId, JobStatus.Failed);
        await jobs.UpdateStatusAsync(jobId, JobStatus.Cancelled);

        var job = await _fixture.GetJobAsync(jobId);
        Assert.That(job.Status, Is.EqualTo(JobStatus.Success),
            "Success is truly final — nothing overrides it");
    }
}
