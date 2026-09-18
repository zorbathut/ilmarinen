using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
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

    [Test]
    public async Task Worker_Disconnect_JobStaysRunning()
    {
        _repo.AddFile("pipeline.csx", """
            Step("slow")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Exec("sleep", "30");
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

        // Wait until the job is running
        await _fixture.WaitForJobStatusAsync(jobId, JobStatus.Running);

        // Disconnect the worker
        await _fixture.StopWorkerAsync(preserveIdentity: true);

        // Job should still be Running, not Failed
        var job = await _fixture.GetJobAsync(jobId);
        Assert.That(job.Status, Is.EqualTo(JobStatus.Running),
            "Job should stay Running when worker disconnects, not be marked Failed");
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

    [Test]
    public async Task Worker_Reconnect_NoJob_OrphanFailed()
    {
        _repo.AddFile("pipeline.csx", """
            Step("slow")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Exec("sleep", "30");
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

        // Wait until the job is running
        await _fixture.WaitForJobStatusAsync(jobId, JobStatus.Running);

        // Full stop — worker loses its running job state
        await _fixture.StopWorkerAsync(preserveIdentity: true);

        // Restart worker — it has no _currentJobId, so it will report null. The server should detect the orphaned job and mark it failed.
        await _fixture.RestartWorkerAsync();

        // The job should be failed because the worker reconnected without it
        var job = await _fixture.WaitForJobCompletionAsync(jobId, timeoutMs: 30000);
        Assert.That(job.Status, Is.EqualTo(JobStatus.Failed),
            "Orphaned job should be marked Failed when worker reconnects without it");
    }

    [Test]
    public async Task Cancel_WhileWorkerDisconnected()
    {
        _repo.AddFile("pipeline.csx", """
            Step("slow")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Exec("sleep", "30");
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

        // Wait until the job is running
        await _fixture.WaitForJobStatusAsync(jobId, JobStatus.Running);

        // Disconnect the worker
        await _fixture.StopWorkerAsync(preserveIdentity: true);

        // Cancel the job while worker is disconnected
        var cancelled = await _fixture.CancelJobAsync(jobId);
        Assert.That(cancelled, Is.True, "Should be able to cancel a job while worker is disconnected");

        var job = await _fixture.GetJobAsync(jobId);
        Assert.That(job.Status, Is.EqualTo(JobStatus.Cancelled),
            "Job should be Cancelled immediately without waiting for worker");
    }

    [Test]
    public async Task Cancel_WhileWorkerDisconnected_NotifiesSubscribers()
    {
        var subscriber = await _fixture.RegisterSubscriberAsync(
            new SubscriberRegistration { Name = "cancel-signal-test" });

        _repo.AddFile("pipeline.csx", """
            Step("slow")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Exec("sleep", "30");
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

        await _fixture.StopWorkerAsync(preserveIdentity: true);

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
