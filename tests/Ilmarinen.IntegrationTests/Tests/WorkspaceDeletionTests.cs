using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using NUnit.Framework;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
[Category("Integration")]
public class WorkspaceDeletionTests
{
    private IntegrationTestFixture _fixture = null!;
    private TestGitRepository _repo = null!;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

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
    public async Task GetWorkers_AfterJobWithWorkspace_IncludesWorkspaceName()
    {
        // Arrange - pipeline with Workspace("report-ws")
        _repo.AddFile("pipeline.csx", """
            Workspace("report-ws");
            Step("check")
                .Image("alpine:latest")
                .Run(async ctx => await ctx.Exec("echo", "Hello"));
            """);
        _repo.Commit("Add workspace pipeline");

        var submission = new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        };

        // Act
        var jobId = await _fixture.SubmitJobAsync(submission);
        var job = await _fixture.WaitForJobCompletionAsync(jobId);
        Assert.That(job.Status, Is.EqualTo(JobStatus.Success));

        // Assert - GET /api/workers includes workspace in list
        var response = await _fixture.HttpClient.GetAsync("/api/workers");
        response.EnsureSuccessStatusCode();

        var workers = await response.Content.ReadFromJsonAsync<List<WorkerView>>(JsonOptions);
        Assert.That(workers, Is.Not.Empty);

        var worker = workers![0];
        Assert.That(worker.Workspaces.Select(w => w.Name), Does.Contain("report-ws"));
    }

    [Test]
    public async Task GetWorkers_AfterJobWithWorkspace_ReportsLastUseAndTheSpaceDeletingItFrees()
    {
        // Random, so neither git nor the filesystem can store it in less than its size
        const int blobSize = 1024 * 1024;
        var blob = new byte[blobSize];
        Random.Shared.NextBytes(blob);
        _repo.AddBinaryFile("blob.bin", blob);
        _repo.AddFile("pipeline.csx", """
            Workspace("usage-ws");
            Step("check")
                .Image("alpine:latest")
                .Run(async ctx => await ctx.Exec("echo", "Hello"));
            """);
        _repo.Commit("Add workspace pipeline with a large blob");

        var jobId = await _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });
        var job = await _fixture.WaitForJobCompletionAsync(jobId);
        Assert.That(job.Status, Is.EqualTo(JobStatus.Success));

        // Disk usage is measured in the background after the job, so it lands some time after the result
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        WorkerView? worker = null;
        WorkspaceView? workspace = null;
        while (workspace?.Measured != true)
        {
            Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "the worker never reported the disk usage of usage-ws");
            await Task.Delay(200);
            var workers = await _fixture.HttpClient.GetFromJsonAsync<List<WorkerView>>("/api/workers", JsonOptions);
            worker = workers![0];
            workspace = worker.Workspaces.SingleOrDefault(w => w.Name == "usage-ws");
        }

        Assert.That(workspace.LastUsed, Is.GreaterThan(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5)));
        // The checkout holds its own copy of the blob, but the clone hard-links the mirror's copy of the object instead of duplicating it
        Assert.That(workspace.SizeBytes, Is.InRange((long)blobSize, blobSize * 3L / 2));
        Assert.That(worker!.MirrorsSizeBytes, Is.GreaterThanOrEqualTo((long)blobSize));
    }

    [Test]
    public async Task DeleteWorkspace_ExistingWorkspace_Returns200AndDeletes()
    {
        // Arrange - create workspace via a job
        _repo.AddFile("pipeline.csx", """
            Workspace("del-ws");
            Step("setup")
                .Image("alpine:latest")
                .Run(async ctx => await ctx.Exec("echo", "Setup"));
            """);
        _repo.Commit("Add workspace pipeline");

        var jobId = await _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });
        var job = await _fixture.WaitForJobCompletionAsync(jobId);
        Assert.That(job.Status, Is.EqualTo(JobStatus.Success));

        // Verify workspace exists
        var wsPath = Path.Combine(_fixture.WorkerWorkspacePath!, "del-ws");
        Assert.That(Directory.Exists(wsPath), Is.True);

        // Get worker ID
        var workersResponse = await _fixture.HttpClient.GetAsync("/api/workers");
        var workers = await workersResponse.Content.ReadFromJsonAsync<List<WorkerView>>(JsonOptions);
        var workerId = workers![0].Id;

        // Act - delete workspace
        var deleteResponse = await _fixture.HttpClient.DeleteAsync(
            $"/api/workers/{workerId}/workspaces/del-ws");

        // Assert
        Assert.That(deleteResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(Directory.Exists(wsPath), Is.False);
    }

    [Test]
    public async Task DeleteWorkspace_NonExistentWorkspace_Returns404()
    {
        // Get worker ID
        var workersResponse = await _fixture.HttpClient.GetAsync("/api/workers");
        var workers = await workersResponse.Content.ReadFromJsonAsync<List<WorkerView>>(JsonOptions);
        var workerId = workers![0].Id;

        // Act
        var response = await _fixture.HttpClient.DeleteAsync(
            $"/api/workers/{workerId}/workspaces/no-such-ws");

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task DeleteWorkspace_InvalidName_Returns400()
    {
        // Get worker ID
        var workersResponse = await _fixture.HttpClient.GetAsync("/api/workers");
        var workers = await workersResponse.Content.ReadFromJsonAsync<List<WorkerView>>(JsonOptions);
        var workerId = workers![0].Id;

        // Act - path traversal attempt
        var response = await _fixture.HttpClient.DeleteAsync(
            $"/api/workers/{workerId}/workspaces/..%2Fescape");

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task DeleteWorkspace_NonExistentWorker_Returns404()
    {
        var fakeWorkerId = Guid.CreateVersion7();

        // Act
        var response = await _fixture.HttpClient.DeleteAsync(
            $"/api/workers/{fakeWorkerId}/workspaces/some-ws");

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
