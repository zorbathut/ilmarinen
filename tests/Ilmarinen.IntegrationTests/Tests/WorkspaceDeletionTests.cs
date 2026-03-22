using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using NUlid;
using NUnit.Framework;
using System.Collections.Generic;
using System.IO;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;

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
        Converters = { new UlidJsonConverter() }
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
        Assert.That(worker.Workspaces, Does.Contain("report-ws"));
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
        var fakeWorkerId = Ulid.NewUlid();

        // Act
        var response = await _fixture.HttpClient.DeleteAsync(
            $"/api/workers/{fakeWorkerId}/workspaces/some-ws");

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
