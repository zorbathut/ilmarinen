using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using NUnit.Framework;

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
