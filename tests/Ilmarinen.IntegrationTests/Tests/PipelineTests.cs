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
    public async Task CreatePipeline_WithSchedule_StoresSchedule()
    {
        var pipeline = await _fixture.CreatePipelineAsync(new PipelineSubmission
        {
            Name = "scheduled-pipeline",
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx",
            Schedule = "0 2 * * *"
        });

        Assert.That(pipeline.Schedule, Is.EqualTo("0 2 * * *"));

        var fetched = await _fixture.GetPipelineAsync(pipeline.Id);
        Assert.That(fetched.Schedule, Is.EqualTo("0 2 * * *"));
    }

    [Test]
    public async Task CreatePipeline_WithInvalidSchedule_Returns400()
    {
        var response = await _fixture.HttpClient.PostAsJsonAsync(
            "/api/pipelines",
            new PipelineSubmission
            {
                Name = "bad-schedule",
                RepoUrl = _repo.Url,
                Ref = "master",
                ScriptPath = "pipeline.csx",
                Schedule = "not a cron expression"
            },
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new UlidJsonConverter() }
            });

        Assert.That((int)response.StatusCode, Is.EqualTo(400));
    }

    [Test]
    public async Task UpdatePipeline_Schedule_CanSetAndClear()
    {
        var pipeline = await _fixture.CreatePipelineAsync(new PipelineSubmission
        {
            Name = "schedule-update-test",
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        Assert.That(pipeline.Schedule, Is.Null);

        var withSchedule = await _fixture.UpdatePipelineAsync(pipeline.Id, new PipelineUpdate
        {
            Schedule = "0 3 * * *"
        });
        Assert.That(withSchedule.Schedule, Is.EqualTo("0 3 * * *"));

        var cleared = await _fixture.UpdatePipelineAsync(pipeline.Id, new PipelineUpdate
        {
            ClearSchedule = true
        });
        Assert.That(cleared.Schedule, Is.Null);
    }

    [Test]
    public async Task ScheduledPipeline_TriggersAutomatically()
    {
        await _fixture.StartWorkerAsync();

        var pipeline = await _fixture.CreatePipelineAsync(new PipelineSubmission
        {
            Name = "auto-trigger-test",
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx",
            Schedule = "* * * * *"
        });

        // Wait for the scheduler to pick it up (runs every 60s)
        var deadline = DateTime.UtcNow.AddSeconds(90);
        Ilmarinen.Protocol.Responses.JobInfo? job = null;

        while (DateTime.UtcNow < deadline)
        {
            var jobs = await _fixture.HttpClient.GetFromJsonAsync<List<Ilmarinen.Protocol.Responses.JobInfo>>(
                "/api/jobs",
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new UlidJsonConverter() }
                });

            job = jobs?.FirstOrDefault(j => j.PipelineId == pipeline.Id);
            if (job != null) break;

            await Task.Delay(2000);
        }

        Assert.That(job, Is.Not.Null, "Scheduled pipeline should have triggered a job");
        Assert.That(job!.PipelineId, Is.EqualTo(pipeline.Id));

        // Verify LastTriggeredAt was updated
        var updated = await _fixture.GetPipelineAsync(pipeline.Id);
        Assert.That(updated.LastTriggeredAt, Is.Not.Null);
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
