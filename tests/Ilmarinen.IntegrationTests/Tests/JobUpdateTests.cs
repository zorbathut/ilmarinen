using Ilmarinen.Database;
using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Linq;
using System.Net.Http.Json;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>PUT /api/jobs/{id}: a queued job's priorities can be changed, and nothing else's.</summary>
[TestFixture]
[Category("Integration")]
public class JobUpdateTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private IntegrationTestFixture _fixture = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new IntegrationTestFixture();
        await _fixture.SetupAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _fixture.DisposeAsync();
    }

    private HttpClient Http
    {
        get { return _fixture.HttpClient; }
    }

    /// <summary>A Medium job needing a Medium worker, queued behind the scheduler's back; with no workers registered nothing could take it anyway.</summary>
    private async Task<Guid> QueueQuietlyAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
        var id = (await jobs.CreateAsync(new JobSubmission
        {
            RepoUrl = "https://example.invalid/repo.git",
            Ref = "master",
            ScriptPath = "pipeline.csx",
            GitTokenMode = GitTokenMode.None,
            Priority = StandardPriority.Medium,
            MinWorkerPriority = StandardPriority.Medium
        })).Id;
        await jobs.UpdateStatusAsync(id, JobStatus.Queued);
        return id;
    }

    /// <summary>Straight into the row: Running is otherwise reached only by assignment to a connected worker, and these tests are about the API, not how a job got where it is.</summary>
    private async Task SetStatusAsync(Guid id, JobStatus status)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        await db.Jobs.Where(j => j.Id == id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, status));
    }

    private async Task<JobInfo> GetAsync(Guid id)
    {
        return (await Http.GetFromJsonAsync<JobInfo>($"/api/jobs/{id}", JsonOptions))!;
    }

    private Task<HttpResponseMessage> PutAsync(Guid id, JobUpdate update)
    {
        return Http.PutAsJsonAsync($"/api/jobs/{id}", update);
    }

    [Test]
    public async Task BothPriorities_AreChanged()
    {
        var id = await QueueQuietlyAsync();

        var response = await PutAsync(id, new JobUpdate { Priority = StandardPriority.High, MinWorkerPriority = StandardPriority.Low });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var returned = (await response.Content.ReadFromJsonAsync<JobInfo>(JsonOptions))!;
        Assert.That(returned.Id, Is.EqualTo(id));
        Assert.That(returned.Priority.Name, Is.EqualTo(StandardPriority.High), "the answer is the updated job");
        var job = await GetAsync(id);
        Assert.That(job.Priority.Name, Is.EqualTo(StandardPriority.High));
        Assert.That(job.MinWorkerPriority?.Name, Is.EqualTo(StandardPriority.Low));
    }

    [Test]
    public async Task PriorityAlone_LeavesTheMinimum()
    {
        var id = await QueueQuietlyAsync();

        var response = await PutAsync(id, new JobUpdate { Priority = StandardPriority.Low });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var job = await GetAsync(id);
        Assert.That(job.Priority.Name, Is.EqualTo(StandardPriority.Low));
        Assert.That(job.MinWorkerPriority?.Name, Is.EqualTo(StandardPriority.Medium));
    }

    [Test]
    public async Task MinimumAlone_LeavesThePriority()
    {
        var id = await QueueQuietlyAsync();

        var response = await PutAsync(id, new JobUpdate { MinWorkerPriority = StandardPriority.High });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var job = await GetAsync(id);
        Assert.That(job.Priority.Name, Is.EqualTo(StandardPriority.Medium));
        Assert.That(job.MinWorkerPriority?.Name, Is.EqualTo(StandardPriority.High));
    }

    [Test]
    public async Task AnyWorker_ClearsTheMinimum()
    {
        var id = await QueueQuietlyAsync();

        var response = await PutAsync(id, new JobUpdate { MinWorkerPriority = PriorityNames.AnyWorker });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await GetAsync(id)).MinWorkerPriority, Is.Null);
    }

    [Test]
    public async Task AnEmptyUpdate_IsABadRequest()
    {
        var id = await QueueQuietlyAsync();

        var response = await Http.PutAsync($"/api/jobs/{id}", new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task AnUnknownJobPriority_IsABadRequest_AndChangesNothing()
    {
        var id = await QueueQuietlyAsync();

        var response = await PutAsync(id, new JobUpdate { Priority = "Ultra", MinWorkerPriority = StandardPriority.Low });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var job = await GetAsync(id);
        Assert.That(job.Priority.Name, Is.EqualTo(StandardPriority.Medium));
        Assert.That(job.MinWorkerPriority?.Name, Is.EqualTo(StandardPriority.Medium));
    }

    [Test]
    public async Task AnUnknownWorkerPriority_IsABadRequest()
    {
        var id = await QueueQuietlyAsync();

        var response = await PutAsync(id, new JobUpdate { MinWorkerPriority = "Ultra" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task AnUnknownJob_IsNotFound()
    {
        var response = await PutAsync(Guid.NewGuid(), new JobUpdate { Priority = StandardPriority.High });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [TestCase(JobStatus.Running)]
    [TestCase(JobStatus.Cancelled)]
    [TestCase(JobStatus.Success)]
    public async Task AJobNoLongerQueued_IsAConflict_AndUnchanged(JobStatus status)
    {
        var id = await QueueQuietlyAsync();
        await SetStatusAsync(id, status);

        var response = await PutAsync(id, new JobUpdate { Priority = StandardPriority.High, MinWorkerPriority = PriorityNames.AnyWorker });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var job = await GetAsync(id);
        Assert.That(job.Priority.Name, Is.EqualTo(StandardPriority.Medium));
        Assert.That(job.MinWorkerPriority?.Name, Is.EqualTo(StandardPriority.Medium));
    }
}
