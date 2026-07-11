using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
[Category("Integration")]
public class PipelineTests
{
    private IntegrationTestFixture _fixture = null!;
    private TestGitRepository _repo = null!;
    private RepositoryInfo _repository = null!;

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

        _repository = await _fixture.CreateRepositoryAsync(new RepositorySubmission
        {
            Name = "test-repo-" + Guid.CreateVersion7().ToString()[..8],
            RepoUrl = _repo.Url
        });
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
            RepositoryId = _repository.Id,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        };

        var pipeline = await _fixture.CreatePipelineAsync(submission);

        Assert.That(pipeline.Name, Is.EqualTo("test-pipeline"));
        Assert.That(pipeline.RepositoryId, Is.EqualTo(_repository.Id));
        Assert.That(pipeline.RepoUrl, Is.EqualTo(_repo.Url));
        Assert.That(pipeline.DefaultRef, Is.EqualTo("master"));
        Assert.That(pipeline.ScriptPath, Is.EqualTo("pipeline.csx"));
        Assert.That(pipeline.Id, Is.Not.EqualTo(Guid.Empty));
    }

    [Test]
    public async Task GetPipeline_AfterCreation_ReturnsPipelineInfo()
    {
        var submission = new PipelineSubmission
        {
            Name = "get-test",
            RepositoryId = _repository.Id,
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
            RepositoryId = _repository.Id,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        await _fixture.CreatePipelineAsync(new PipelineSubmission
        {
            Name = "pipeline-b",
            RepositoryId = _repository.Id,
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
            RepositoryId = _repository.Id,
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
            RepositoryId = _repository.Id,
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
            RepositoryId = _repository.Id,
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
        var fakeId = Guid.CreateVersion7();
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
            RepositoryId = _repository.Id,
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
    public async Task UpdatePipeline_ChangeRepository()
    {
        var pipeline = await _fixture.CreatePipelineAsync(new PipelineSubmission
        {
            Name = "repo-change-test",
            RepositoryId = _repository.Id,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        var newRepo = await _fixture.CreateRepositoryAsync(new RepositorySubmission
        {
            Name = "other-repo",
            RepoUrl = _repo.Url
        });

        var updated = await _fixture.UpdatePipelineAsync(pipeline.Id, new PipelineUpdate
        {
            RepositoryId = newRepo.Id
        });

        Assert.That(updated.RepositoryId, Is.EqualTo(newRepo.Id));
        Assert.That(updated.RepositoryName, Is.EqualTo("other-repo"));
    }

    [Test]
    public async Task Repository_SetAndRemoveGitToken()
    {
        Assert.That(_repository.HasGitToken, Is.False);

        var withToken = await _fixture.UpdateRepositoryAsync(_repository.Id, new RepositoryUpdate
        {
            UpdateGitToken = true,
            GitToken = "ghp_test123"
        });

        Assert.That(withToken.HasGitToken, Is.True);

        var withoutToken = await _fixture.UpdateRepositoryAsync(_repository.Id, new RepositoryUpdate
        {
            UpdateGitToken = true,
            GitToken = null
        });

        Assert.That(withoutToken.HasGitToken, Is.False);
    }

    [Test]
    public async Task UpdatePipeline_NonExistent_Returns404()
    {
        var fakeId = Guid.CreateVersion7();
        var response = await _fixture.HttpClient.PutAsJsonAsync(
            $"/api/pipelines/{fakeId}",
            new PipelineUpdate { Name = "nope" },
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

        Assert.That((int)response.StatusCode, Is.EqualTo(404));
    }

    [Test]
    public async Task CreatePipeline_WithSchedule_StoresSchedule()
    {
        var pipeline = await _fixture.CreatePipelineAsync(new PipelineSubmission
        {
            Name = "scheduled-pipeline",
            RepositoryId = _repository.Id,
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
                RepositoryId = _repository.Id,
                Ref = "master",
                ScriptPath = "pipeline.csx",
                Schedule = "not a cron expression"
            },
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

        Assert.That((int)response.StatusCode, Is.EqualTo(400));
    }

    [Test]
    public async Task UpdatePipeline_Schedule_CanSetAndClear()
    {
        var pipeline = await _fixture.CreatePipelineAsync(new PipelineSubmission
        {
            Name = "schedule-update-test",
            RepositoryId = _repository.Id,
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
            RepositoryId = _repository.Id,
            Ref = "master",
            ScriptPath = "pipeline.csx",
            Schedule = "* * * * *"
        });

        // Worst case is ~125s: the next every-minute cron occurrence can be up to 60s out, the scheduler polls every 60s, and the tick can just miss the occurrence.
        var deadline = DateTime.UtcNow.AddSeconds(150);
        Ilmarinen.Protocol.Responses.JobInfo? job = null;

        while (DateTime.UtcNow < deadline)
        {
            var jobs = await _fixture.HttpClient.GetFromJsonAsync<List<Ilmarinen.Protocol.Responses.JobInfo>>(
                "/api/jobs",
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
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
            RepositoryId = _repository.Id,
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
            });

        Assert.That(response.IsSuccessStatusCode, Is.False);
    }

    [Test]
    public async Task SubmitJob_WithPipelineId_InheritsRepoData()
    {
        await _fixture.StartWorkerAsync();

        var pipeline = await _fixture.CreatePipelineAsync(new PipelineSubmission
        {
            Name = "inherit-test",
            RepositoryId = _repository.Id,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        // Submit a job referencing the pipeline — no RepoUrl/Ref/ScriptPath
        var jobId = await _fixture.SubmitJobAsync(new JobSubmission
        {
            PipelineId = pipeline.Id,
            GitTokenMode = GitTokenMode.Inherit
        });

        var job = await _fixture.GetJobAsync(jobId);

        Assert.That(job.RepoUrl, Is.EqualTo(_repo.Url));
        Assert.That(job.Ref, Is.EqualTo("master"));
        Assert.That(job.ScriptPath, Is.EqualTo("pipeline.csx"));
        Assert.That(job.PipelineId, Is.EqualTo(pipeline.Id));
        Assert.That(job.GitTokenMode, Is.EqualTo(GitTokenMode.Inherit));
    }

    [Test]
    public async Task SubmitJob_WithPipelineId_CanOverrideRef()
    {
        var pipeline = await _fixture.CreatePipelineAsync(new PipelineSubmission
        {
            Name = "override-ref-test",
            RepositoryId = _repository.Id,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        var jobId = await _fixture.SubmitJobAsync(new JobSubmission
        {
            PipelineId = pipeline.Id,
            Ref = "master",
            GitTokenMode = GitTokenMode.None
        });

        var job = await _fixture.GetJobAsync(jobId);
        Assert.That(job.Ref, Is.EqualTo("master"));
        Assert.That(job.GitTokenMode, Is.EqualTo(GitTokenMode.None));
    }

    [Test]
    public async Task SubmitJob_InheritWithoutPipeline_Returns400()
    {
        var response = await _fixture.HttpClient.PostAsJsonAsync(
            "/api/jobs",
            new JobSubmission
            {
                RepoUrl = _repo.Url,
                Ref = "master",
                ScriptPath = "pipeline.csx",
                GitTokenMode = GitTokenMode.Inherit
            },
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

        Assert.That((int)response.StatusCode, Is.EqualTo(400));
    }

    [Test]
    public async Task SubmitJob_NoPipelineNoRepoUrl_Returns400()
    {
        var response = await _fixture.HttpClient.PostAsJsonAsync(
            "/api/jobs",
            new JobSubmission
            {
                Ref = "master",
                ScriptPath = "pipeline.csx"
            },
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

        Assert.That((int)response.StatusCode, Is.EqualTo(400));
    }

    [Test]
    public async Task SubmitJob_UnknownPipelineId_Returns400()
    {
        var response = await _fixture.HttpClient.PostAsJsonAsync(
            "/api/jobs",
            new JobSubmission
            {
                PipelineId = Guid.CreateVersion7(),
                GitTokenMode = GitTokenMode.None
            },
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

        Assert.That((int)response.StatusCode, Is.EqualTo(400),
            "a nonexistent pipeline ID is a user mistake, not a server bug");
    }

    [Test]
    public async Task MultiplePipelines_SameRepository()
    {
        var pipeline1 = await _fixture.CreatePipelineAsync(new PipelineSubmission
        {
            Name = "pipeline-shared-1",
            RepositoryId = _repository.Id,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        var pipeline2 = await _fixture.CreatePipelineAsync(new PipelineSubmission
        {
            Name = "pipeline-shared-2",
            RepositoryId = _repository.Id,
            Ref = "main",
            ScriptPath = "other.csx"
        });

        Assert.That(pipeline1.RepositoryId, Is.EqualTo(pipeline2.RepositoryId));
        Assert.That(pipeline1.RepoUrl, Is.EqualTo(pipeline2.RepoUrl));
        Assert.That(pipeline1.RepositoryName, Is.EqualTo(pipeline2.RepositoryName));
    }
}
