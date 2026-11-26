using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
[Category("Integration")]
public class PersistentWorkspaceTests
{
    private IntegrationTestFixture _fixture = null!;
    private TestGitRepository _repo = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new IntegrationTestFixture();
        await _fixture.SetupAsync();
        await _fixture.StartWorkerAsync();

        _repo = new TestGitRepository();
    }

    [TearDown]
    public async Task TearDown()
    {
        _repo.Dispose();
        await _fixture.DisposeAsync();
    }

    [Test]
    public async Task PersistentWorkspace_CreatesNamedDirectory()
    {
        // Arrange - pipeline with Workspace("test-ws")
        _repo.AddFile("pipeline.csx", """
            Workspace("test-ws");
            Step("check")
                .Image("alpine:latest")
                .Run(async ctx => await ctx.Exec("echo", "Hello"));
            """);
        _repo.Commit("Add persistent workspace pipeline");

        var submission = new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        };

        // Act
        var jobId = await _fixture.SubmitJobAsync(submission);
        var job = await _fixture.WaitForJobCompletionAsync(jobId);

        // Assert
        Assert.That(job.Status, Is.EqualTo(JobStatus.Success));

        // Verify workspace directory exists at {WorkspacePath}/test-ws/
        var workspacePath = Path.Combine(_fixture.WorkerWorkspacePath!, "test-ws");
        Assert.That(Directory.Exists(workspacePath), Is.True,
            $"Expected persistent workspace directory at {workspacePath}");
    }

    [Test]
    public async Task PersistentWorkspace_PersistsAfterJob()
    {
        // Arrange - pipeline with persistent workspace
        _repo.AddFile("pipeline.csx", """
            Workspace("persist-test");
            Step("check")
                .Image("alpine:latest")
                .Run(async ctx => await ctx.Exec("echo", "Job running"));
            """);
        _repo.Commit("Add persistent workspace pipeline");

        var submission = new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        };

        // Act - run job
        var jobId = await _fixture.SubmitJobAsync(submission);
        var job = await _fixture.WaitForJobCompletionAsync(jobId);

        // Assert - job succeeded and workspace persists
        Assert.That(job.Status, Is.EqualTo(JobStatus.Success), "Job should succeed");

        // Verify workspace directory persists (not deleted like ephemeral)
        var workspacePath = Path.Combine(_fixture.WorkerWorkspacePath!, "persist-test");
        Assert.That(Directory.Exists(workspacePath), Is.True,
            $"Persistent workspace should exist at {workspacePath} after job completion");

        // Verify .git directory exists (confirms it's a proper clone)
        var gitDir = Path.Combine(workspacePath, ".git");
        Assert.That(Directory.Exists(gitDir), Is.True,
            "Workspace should contain .git directory");
    }

    // The step waits, in its container, for a file the test creates in the workspace: the job holds its workspace until the test lets it go, and cancelling the job ends the wait with the container.
    [Test]
    public async Task AWorkspaceInUse_IsShownSo_AndDeletingIt_WaitsForTheJob()
    {
        _repo.AddFile("pipeline.csx", """
            Workspace("gated-ws");
            Step("wait")
                .Image("alpine:latest")
                .Run(async ctx => await ctx.Shell("while [ ! -f /workspace/.gate ]; do sleep 0.1; done"));
            """);
        _repo.Commit("Add gated pipeline");
        var submission = new JobSubmission { RepoUrl = _repo.Url, Ref = "master", ScriptPath = "pipeline.csx" };

        // The first run makes the workspace, which the worker lists only once that job is done; the gate is already open for it.
        var first = await _fixture.SubmitJobAsync(submission);
        var gate = Path.Combine(_fixture.WorkerWorkspacePath!, "gated-ws", ".gate");
        await WaitUntilAsync(() => Task.FromResult(Directory.Exists(Path.GetDirectoryName(gate))), "the first job to make its workspace");
        File.WriteAllText(gate, "");
        Assert.That((await _fixture.WaitForJobCompletionAsync(first)).Status, Is.EqualTo(JobStatus.Success));
        File.Delete(gate);

        var workers = _fixture.Services.GetRequiredService<WorkerRepository>();
        var workerId = (await workers.GetAllAsync()).Single().Id;
        async Task<bool> InUseAsync()
        {
            return (await workers.GetByIdAsync(workerId))!.Workspaces.Single(w => w.Name == "gated-ws").InUse;
        }

        var jobId = await _fixture.SubmitJobAsync(submission);
        try
        {
            await WaitUntilAsync(InUseAsync, "the running job's workspace to be shown in use");

            var queued = await _fixture.HttpClient.DeleteAsync($"/api/workers/{workerId}/workspaces/gated-ws");
            Assert.That(queued.StatusCode, Is.EqualTo(HttpStatusCode.Accepted), "queued until the job ends");
            Assert.That((await workers.GetByIdAsync(workerId))!.Workspaces.Single(w => w.Name == "gated-ws").DeletePending, Is.True);
            Assert.That(Directory.Exists(Path.GetDirectoryName(gate)), Is.True, "not while the job is using it");
        }
        finally
        {
            // Released whatever happened above, so a failure can't leave the job, and the fixture's teardown, waiting forever.
            File.WriteAllText(gate, "");
        }

        Assert.That((await _fixture.WaitForJobCompletionAsync(jobId)).Status, Is.EqualTo(JobStatus.Success));
        // The outcome is reported ahead of the idle state that lets the workspace go, and the delete follows that.
        await WaitUntilAsync(async () => !Directory.Exists(Path.GetDirectoryName(gate)) && (await workers.GetByIdAsync(workerId))!.Workspaces.All(w => w.Name != "gated-ws"), "the queued delete");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (!await condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(60))
            {
                Assert.Fail($"Timed out waiting for {what}");
            }
            await Task.Delay(50);
        }
    }

    [Test]
    public async Task EphemeralWorkspace_DeletedAfterJob()
    {
        // Arrange - pipeline without Workspace() call
        _repo.AddFile("pipeline.csx", """
            Step("temp")
                .Image("alpine:latest")
                .Run(async ctx => await ctx.Exec("echo", "Ephemeral"));
            """);
        _repo.Commit("Add ephemeral pipeline");

        var submission = new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        };

        // Act
        var jobId = await _fixture.SubmitJobAsync(submission);
        var job = await _fixture.WaitForJobCompletionAsync(jobId);

        // Assert
        Assert.That(job.Status, Is.EqualTo(JobStatus.Success));

        // Verify job-specific directory was deleted
        var jobWorkspace = Path.Combine(_fixture.WorkerWorkspacePath!, jobId.ToString());
        Assert.That(Directory.Exists(jobWorkspace), Is.False,
            $"Ephemeral workspace {jobWorkspace} should be deleted after job completion");
    }

    [Test]
    public async Task PersistentWorkspace_FailsOnRepoMismatch()
    {
        // Arrange - create workspace with first repo
        _repo.AddFile("pipeline.csx", """
            Workspace("shared-ws");
            Step("first")
                .Image("alpine:latest")
                .Run(async ctx => await ctx.Exec("echo", "First repo"));
            """);
        _repo.Commit("First repo pipeline");

        var job1Id = await _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });
        var job1 = await _fixture.WaitForJobCompletionAsync(job1Id);
        Assert.That(job1.Status, Is.EqualTo(JobStatus.Success), "First job should succeed");

        // Create second repo trying to use same workspace name
        using var repo2 = new TestGitRepository();
        repo2.AddFile("pipeline.csx", """
            Workspace("shared-ws");
            Step("second")
                .Image("alpine:latest")
                .Run(async ctx => await ctx.Exec("echo", "Second repo"));
            """);
        repo2.Commit("Second repo pipeline");

        // Act - submit from different repo with same workspace name
        var job2Id = await _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = repo2.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });
        var job2 = await _fixture.WaitForJobCompletionAsync(job2Id);

        // Assert - should fail due to repo mismatch
        Assert.That(job2.Status, Is.EqualTo(JobStatus.Failed),
            "Second job should fail because workspace belongs to a different repository");
    }
}
