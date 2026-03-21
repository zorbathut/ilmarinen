using System.Net.Http.Json;
using System.Text.Json;
using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using NUnit.Framework;
using NUlid;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
[Category("Integration")]
public class PipelineTests
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
            Step("hello")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Exec("echo", "Hello from pipeline test!");
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
    public async Task CreatePipeline_ReturnsPipelineInfo()
    {
        var submission = new PipelineSubmission
        {
            Name = "test-pipeline",
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        };

        var pipeline = await _fixture.CreatePipelineAsync(submission);

        Assert.That(pipeline.Name, Is.EqualTo("test-pipeline"));
        Assert.That(pipeline.RepoUrl, Is.EqualTo(_repo.Url));
        Assert.That(pipeline.DefaultRef, Is.EqualTo("master"));
        Assert.That(pipeline.ScriptPath, Is.EqualTo("pipeline.csx"));
        Assert.That(pipeline.Id, Is.Not.EqualTo(default(Ulid)));
    }

    [Test]
    public async Task GetPipeline_AfterCreation_ReturnsPipelineInfo()
    {
        var submission = new PipelineSubmission
        {
            Name = "get-test",
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        };

        var created = await _fixture.CreatePipelineAsync(submission);
        var fetched = await _fixture.GetPipelineAsync(created.Id);

        Assert.That(fetched.Id, Is.EqualTo(created.Id));
        Assert.That(fetched.Name, Is.EqualTo("get-test"));
    }

    [Test]
    public async Task ListPipelines_ReturnsAll()
    {
        await _fixture.CreatePipelineAsync(new PipelineSubmission
        {
            Name = "pipeline-a",
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        await _fixture.CreatePipelineAsync(new PipelineSubmission
        {
            Name = "pipeline-b",
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        var all = await _fixture.GetAllPipelinesAsync();

        Assert.That(all, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task DeletePipeline_Succeeds()
    {
        var pipeline = await _fixture.CreatePipelineAsync(new PipelineSubmission
        {
            Name = "to-delete",
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        var deleted = await _fixture.DeletePipelineAsync(pipeline.Id);
        Assert.That(deleted, Is.True);

        var response = await _fixture.HttpClient.GetAsync($"/api/pipelines/{pipeline.Id}");
        Assert.That((int)response.StatusCode, Is.EqualTo(404));
    }

    [Test]
    public async Task TriggerPipeline_CreatesAndExecutesJob()
    {
        await _fixture.StartWorkerAsync();

        var pipeline = await _fixture.CreatePipelineAsync(new PipelineSubmission
        {
            Name = "trigger-test",
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        var jobId = await _fixture.TriggerPipelineAsync(pipeline.Id);
        var completedJob = await _fixture.WaitForJobCompletionAsync(jobId);

        Assert.That(completedJob.Status, Is.EqualTo(JobStatus.Success));
        Assert.That(completedJob.PipelineId, Is.EqualTo(pipeline.Id));
        Assert.That(completedJob.PipelineName, Is.EqualTo("trigger-test"));
    }

    [Test]
    public async Task TriggerPipeline_WithRefOverride_UsesOverriddenRef()
    {
        var pipeline = await _fixture.CreatePipelineAsync(new PipelineSubmission
        {
            Name = "ref-override-test",
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        var jobId = await _fixture.TriggerPipelineAsync(pipeline.Id, new PipelineTrigger { Ref = "master" });
        var job = await _fixture.GetJobAsync(jobId);

        Assert.That(job.Ref, Is.EqualTo("master"));
        Assert.That(job.PipelineId, Is.EqualTo(pipeline.Id));
    }

    [Test]
    public async Task TriggerPipeline_NonExistent_Returns404()
    {
        var fakeId = Ulid.NewUlid();
        var response = await _fixture.HttpClient.PostAsJsonAsync(
            $"/api/pipelines/{fakeId}/trigger",
            new PipelineTrigger(),
            new System.Text.Json.JsonSerializerOptions());

        Assert.That((int)response.StatusCode, Is.EqualTo(404));
    }

    [Test]
    public async Task UpdatePipeline_ChangesFields()
    {
        var pipeline = await _fixture.CreatePipelineAsync(new PipelineSubmission
        {
            Name = "update-test",
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        var updated = await _fixture.UpdatePipelineAsync(pipeline.Id, new PipelineUpdate
        {
            Name = "updated-name",
            Ref = "main"
        });

        Assert.That(updated.Name, Is.EqualTo("updated-name"));
        Assert.That(updated.DefaultRef, Is.EqualTo("main"));
        Assert.That(updated.RepoUrl, Is.EqualTo(_repo.Url));
        Assert.That(updated.ScriptPath, Is.EqualTo("pipeline.csx"));
    }

    [Test]
    public async Task UpdatePipeline_SetAndRemoveGitToken()
    {
        var pipeline = await _fixture.CreatePipelineAsync(new PipelineSubmission
        {
            Name = "token-test",
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        Assert.That(pipeline.HasGitToken, Is.False);

        var withToken = await _fixture.UpdatePipelineAsync(pipeline.Id, new PipelineUpdate
        {
            UpdateGitToken = true,
            GitToken = "ghp_test123"
        });

        Assert.That(withToken.HasGitToken, Is.True);

        var withoutToken = await _fixture.UpdatePipelineAsync(pipeline.Id, new PipelineUpdate
        {
            UpdateGitToken = true,
            GitToken = null
        });

        Assert.That(withoutToken.HasGitToken, Is.False);
    }

    [Test]
    public async Task UpdatePipeline_NonExistent_Returns404()
    {
        var fakeId = Ulid.NewUlid();
        var response = await _fixture.HttpClient.PutAsJsonAsync(
            $"/api/pipelines/{fakeId}",
            new PipelineUpdate { Name = "nope" },
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new Ilmarinen.Protocol.UlidJsonConverter() }
            });

        Assert.That((int)response.StatusCode, Is.EqualTo(404));
    }

    [Test]
    public async Task CreatePipeline_DuplicateName_Returns409OrError()
    {
        var submission = new PipelineSubmission
        {
            Name = "duplicate-name",
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        };

        await _fixture.CreatePipelineAsync(submission);

        var response = await _fixture.HttpClient.PostAsJsonAsync(
            "/api/pipelines",
            submission,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new Ilmarinen.Protocol.UlidJsonConverter() }
            });

        Assert.That(response.IsSuccessStatusCode, Is.False);
    }
}
