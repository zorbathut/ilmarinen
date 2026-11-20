using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// Managing the lists of priorities over the REST surface: adding, renaming, reordering, choosing the default and deleting, and what each does to the workers, pipelines and jobs using them.
/// </summary>
[TestFixture]
[Category("Integration")]
public class PriorityTests
{
    private IntegrationTestFixture _fixture = null!;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

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

    private async Task<List<PriorityInfo>> ListAsync(string kind)
    {
        return (await Http.GetFromJsonAsync<List<PriorityInfo>>($"/api/priorities/{kind}", JsonOptions))!;
    }

    private async Task<string[]> NamesAsync(string kind)
    {
        return (await ListAsync(kind)).Select(p => p.Name).ToArray();
    }

    private async Task<Guid> IdAsync(string kind, string name)
    {
        return (await ListAsync(kind)).Single(p => p.Name == name).Id;
    }

    private Task<HttpResponseMessage> CreateAsync(string kind, string name)
    {
        return Http.PostAsJsonAsync($"/api/priorities/{kind}", new PriorityName { Name = name });
    }

    private async Task<Guid> RegisterWorkerAsync(string name, string priority)
    {
        using var scope = _fixture.Services.CreateScope();
        var id = (await scope.ServiceProvider.GetRequiredService<WorkerRegistrationService>().RegisterWorkerAsync(name)).WorkerId;
        var response = await Http.PutAsJsonAsync($"/api/workers/{id}", new WorkerUpdate { Priority = priority });
        response.EnsureSuccessStatusCode();
        return id;
    }

    private async Task<string> WorkerPriorityAsync(Guid workerId)
    {
        var workers = await Http.GetFromJsonAsync<List<WorkerView>>("/api/workers", JsonOptions);
        return workers!.Single(w => w.Id == workerId).Priority.Name;
    }

    private Task<Guid> SubmitAsync(string? priority = null, string? minWorkerPriority = null)
    {
        return _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = "https://example.invalid/repo.git",
            Ref = "master",
            ScriptPath = "pipeline.csx",
            Priority = priority,
            MinWorkerPriority = minWorkerPriority
        });
    }

    [Test]
    public async Task List_IsHighestFirst_WithTheDefaultMarked()
    {
        var workers = await ListAsync("worker");

        Assert.That(workers.Select(p => p.Name), Is.EqualTo(new[] { StandardPriority.High, StandardPriority.Medium, StandardPriority.Low }));
        Assert.That(workers.Single(p => p.IsDefault).Name, Is.EqualTo(StandardPriority.Medium));
    }

    [Test]
    public async Task Create_AddsTheEntryAtTheBottom()
    {
        var response = await CreateAsync("job", "Background");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(await NamesAsync("job"), Is.EqualTo(new[] { StandardPriority.High, StandardPriority.Medium, StandardPriority.Low, "Background" }));
    }

    [TestCase("worker", "medium", Description = "a name in use, in another case")]
    [TestCase("worker", "  ", Description = "blank")]
    [TestCase("worker", "any", Description = "the word a minimum uses for none")]
    [TestCase("worker", "ANY", Description = "that word in another case")]
    public async Task Create_RejectsANameThatCantBeOne(string kind, string name)
    {
        var response = await CreateAsync(kind, name);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await NamesAsync(kind), Has.Length.EqualTo(3));
    }

    [Test]
    public async Task Create_RejectsAnOverlongName_AndAcceptsAnyForAJobPriority()
    {
        var overlong = await CreateAsync("job", new string('x', 65));
        var any = await CreateAsync("job", "any");

        Assert.That(overlong.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(any.StatusCode, Is.EqualTo(HttpStatusCode.Created), "only a minimum worker priority gives the word a meaning");
    }

    [TestCase("5")]
    [TestCase("nonsense")]
    public async Task AnUnknownKind_IsABadRequest(string kind)
    {
        var list = await Http.GetAsync($"/api/priorities/{kind}");
        var create = await CreateAsync(kind, "Anything");

        Assert.That(list.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(create.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Rename_KeepsEverythingUsingIt_AndMayChangeOnlyTheCase()
    {
        var workerId = await RegisterWorkerAsync("renamed-worker", StandardPriority.High);
        var high = await IdAsync("worker", StandardPriority.High);

        var renamed = await Http.PutAsJsonAsync($"/api/priorities/{high}", new PriorityName { Name = "Fast" });
        var recased = await Http.PutAsJsonAsync($"/api/priorities/{high}", new PriorityName { Name = "FAST" });
        var clash = await Http.PutAsJsonAsync($"/api/priorities/{high}", new PriorityName { Name = "low" });
        var unknown = await Http.PutAsJsonAsync($"/api/priorities/{Guid.NewGuid()}", new PriorityName { Name = "Ghost" });

        Assert.That(renamed.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(recased.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(clash.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await WorkerPriorityAsync(workerId), Is.EqualTo("FAST"));
    }

    [Test]
    public async Task Reorder_TakesExactlyTheKindsEntries()
    {
        var ids = (await ListAsync("job")).Select(p => p.Id).ToList();
        var reversed = Enumerable.Reverse(ids).ToList();

        var missingOne = await Http.PutAsJsonAsync("/api/priorities/job/order", reversed.Skip(1).ToList());
        var withAStranger = await Http.PutAsJsonAsync("/api/priorities/job/order", reversed.Skip(1).Append(Guid.NewGuid()).ToList());
        var twice = await Http.PutAsJsonAsync("/api/priorities/job/order", reversed.Skip(1).Append(reversed[1]).ToList());
        var ok = await Http.PutAsJsonAsync("/api/priorities/job/order", reversed);

        Assert.That(missingOne.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(withAStranger.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(twice.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(await NamesAsync("job"), Is.EqualTo(new[] { StandardPriority.Low, StandardPriority.Medium, StandardPriority.High }));
    }

    [Test]
    public async Task MakeDefault_MovesTheMark_AndNewThingsGetIt()
    {
        var low = await IdAsync("worker", StandardPriority.Low);

        var response = await Http.PostAsync($"/api/priorities/{low}/default", null);
        Guid workerId;
        using (var scope = _fixture.Services.CreateScope())
        {
            workerId = (await scope.ServiceProvider.GetRequiredService<WorkerRegistrationService>().RegisterWorkerAsync("fresh-after-default")).WorkerId;
        }

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That((await ListAsync("worker")).Where(p => p.IsDefault).Select(p => p.Name), Is.EqualTo(new[] { StandardPriority.Low }));
        Assert.That(await WorkerPriorityAsync(workerId), Is.EqualTo(StandardPriority.Low));
    }

    [Test]
    public async Task Delete_MovesEverythingUsingItOntoTheReplacement_DefaultIncluded()
    {
        var workerId = await RegisterWorkerAsync("deleted-priority-worker", StandardPriority.Medium);
        var pipeline = await _fixture.CreatePipelineAsync(new PipelineSubmission
        {
            Name = "deleted-priority-pipeline",
            RepositoryId = (await _fixture.CreateRepositoryAsync(new RepositorySubmission { Name = "deleted-priority-repo", RepoUrl = "https://example.invalid/repo.git" })).Id,
            Ref = "master",
            ScriptPath = "pipeline.csx",
            MinWorkerPriority = StandardPriority.Medium
        });
        var queued = await SubmitAsync(minWorkerPriority: StandardPriority.Medium);
        var finished = await SubmitAsync(minWorkerPriority: StandardPriority.Medium);
        Assert.That(await _fixture.CancelJobAsync(finished), Is.True);
        var medium = await IdAsync("worker", StandardPriority.Medium);
        var high = await IdAsync("worker", StandardPriority.High);

        var response = await Http.DeleteAsync($"/api/priorities/{medium}?replacement={high}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(await NamesAsync("worker"), Is.EqualTo(new[] { StandardPriority.High, StandardPriority.Low }));
        Assert.That((await ListAsync("worker")).Single(p => p.IsDefault).Name, Is.EqualTo(StandardPriority.High));
        Assert.That(await WorkerPriorityAsync(workerId), Is.EqualTo(StandardPriority.High));
        Assert.That((await _fixture.GetPipelineAsync(pipeline.Id)).MinWorkerPriority?.Name, Is.EqualTo(StandardPriority.High));
        Assert.That((await _fixture.GetJobAsync(queued)).MinWorkerPriority?.Name, Is.EqualTo(StandardPriority.High));
        Assert.That((await _fixture.GetJobAsync(finished)).MinWorkerPriority?.Name, Is.EqualTo(StandardPriority.High));
    }

    [Test]
    public async Task DeletingAJobPriority_MovesPipelinesAndJobs_AndTheDefault()
    {
        var pipeline = await _fixture.CreatePipelineAsync(new PipelineSubmission
        {
            Name = "deleted-job-priority-pipeline",
            RepositoryId = (await _fixture.CreateRepositoryAsync(new RepositorySubmission { Name = "deleted-job-priority-repo", RepoUrl = "https://example.invalid/repo.git" })).Id,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });
        var job = await SubmitAsync();
        var medium = await IdAsync("job", StandardPriority.Medium);
        var low = await IdAsync("job", StandardPriority.Low);

        (await Http.DeleteAsync($"/api/priorities/{medium}?replacement={low}")).EnsureSuccessStatusCode();
        var afterwards = await SubmitAsync();

        Assert.That((await _fixture.GetPipelineAsync(pipeline.Id)).Priority.Name, Is.EqualTo(StandardPriority.Low));
        Assert.That((await _fixture.GetJobAsync(job)).Priority.Name, Is.EqualTo(StandardPriority.Low));
        Assert.That((await _fixture.GetJobAsync(afterwards)).Priority.Name, Is.EqualTo(StandardPriority.Low), "the default moved with what used it");
    }

    [Test]
    public async Task MakingAJobPriorityTheDefault_IsWhatUnprioritisedJobsAndPipelinesGet()
    {
        var high = await IdAsync("job", StandardPriority.High);
        (await Http.PostAsync($"/api/priorities/{high}/default", null)).EnsureSuccessStatusCode();

        var job = await SubmitAsync();
        var pipeline = await _fixture.CreatePipelineAsync(new PipelineSubmission
        {
            Name = "default-job-priority-pipeline",
            RepositoryId = (await _fixture.CreateRepositoryAsync(new RepositorySubmission { Name = "default-job-priority-repo", RepoUrl = "https://example.invalid/repo.git" })).Id,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        Assert.That((await _fixture.GetJobAsync(job)).Priority.Name, Is.EqualTo(StandardPriority.High));
        Assert.That(pipeline.Priority.Name, Is.EqualTo(StandardPriority.High));
    }

    [Test]
    public async Task AfterARename_RequestsUseTheNewName_AndTheOldOneIsUnknown()
    {
        var high = await IdAsync("job", StandardPriority.High);
        (await Http.PutAsJsonAsync($"/api/priorities/{high}", new PriorityName { Name = "Urgent" })).EnsureSuccessStatusCode();

        var byNewName = await SubmitAsync(priority: "urgent");
        var byOldName = await Http.PostAsJsonAsync("/api/jobs", new JobSubmission { RepoUrl = "https://example.invalid/repo.git", Ref = "master", ScriptPath = "pipeline.csx", Priority = StandardPriority.High });

        Assert.That((await _fixture.GetJobAsync(byNewName)).Priority.Id, Is.EqualTo(high));
        Assert.That(byOldName.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task RenamingAWorkerPriorityToAny_IsRefused()
    {
        var low = await IdAsync("worker", StandardPriority.Low);

        var response = await Http.PutAsJsonAsync($"/api/priorities/{low}", new PriorityName { Name = "Any" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await NamesAsync("worker"), Does.Contain(StandardPriority.Low));
    }

    [Test]
    public async Task GivingAWorkerAPriorityThatIsGone_IsTheCallersMistake()
    {
        var workerId = await RegisterWorkerAsync("gone-priority-worker", StandardPriority.Low);
        var scheduler = _fixture.Services.GetRequiredService<JobScheduler>();

        Assert.ThrowsAsync<Ilmarinen.Server.InvalidSubmissionException>(() => scheduler.SetWorkerPriorityAsync(workerId, Guid.NewGuid()));
        Assert.That(await WorkerPriorityAsync(workerId), Is.EqualTo(StandardPriority.Low));
    }

    [Test]
    public async Task Delete_NeedsAnotherEntryOfTheSameKind()
    {
        var workerMedium = await IdAsync("worker", StandardPriority.Medium);
        var jobHigh = await IdAsync("job", StandardPriority.High);

        var noReplacement = await Http.DeleteAsync($"/api/priorities/{workerMedium}");
        var otherKind = await Http.DeleteAsync($"/api/priorities/{workerMedium}?replacement={jobHigh}");
        var itself = await Http.DeleteAsync($"/api/priorities/{workerMedium}?replacement={workerMedium}");
        var unknown = await Http.DeleteAsync($"/api/priorities/{Guid.NewGuid()}?replacement={workerMedium}");

        Assert.That(noReplacement.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(otherKind.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(itself.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await NamesAsync("worker"), Has.Length.EqualTo(3));
    }

    [Test]
    public async Task Delete_TheLastEntryOfAKind_IsImpossible()
    {
        var low = await IdAsync("job", StandardPriority.Low);
        var medium = await IdAsync("job", StandardPriority.Medium);
        var high = await IdAsync("job", StandardPriority.High);
        (await Http.DeleteAsync($"/api/priorities/{low}?replacement={high}")).EnsureSuccessStatusCode();
        (await Http.DeleteAsync($"/api/priorities/{medium}?replacement={high}")).EnsureSuccessStatusCode();

        // There is nothing left to replace it with, so no request can name a replacement.
        var last = await Http.DeleteAsync($"/api/priorities/{high}?replacement={high}");

        Assert.That(last.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await NamesAsync("job"), Is.EqualTo(new[] { StandardPriority.High }));
    }
}

/// <summary>Dispatch reads the order of the priorities at each dispatch, so changing the lists changes what runs where at once.</summary>
[TestFixture]
[Category("Integration")]
public class PriorityDispatchTests
{
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

    private JobScheduler Scheduler
    {
        get { return _fixture.Services.GetRequiredService<JobScheduler>(); }
    }

    private async Task<DirectedWorker> ReadyWorkerAsync(string connectionId, string priority)
    {
        var worker = await DirectedWorker.RegisterAsync(_fixture, $"pd-{connectionId}");
        await Scheduler.SetWorkerPriorityAsync(worker.WorkerId, await _fixture.PriorityIdAsync(PriorityKind.Worker, priority));
        await worker.ConnectAsync(connectionId);
        await worker.ResumeAsync(worker.Idle());
        return worker;
    }

    private async Task<Guid> EnqueueAsync(string? priority = null, string? minWorkerPriority = null)
    {
        return await Scheduler.EnqueueJobAsync(new JobSubmission
        {
            RepoUrl = "https://example.invalid/repo.git",
            Ref = "master",
            ScriptPath = "pipeline.csx",
            GitTokenMode = GitTokenMode.None,
            Priority = priority,
            MinWorkerPriority = minWorkerPriority
        });
    }

    private async Task<JobStatus> StatusAsync(Guid jobId)
    {
        return (await _fixture.GetJobAsync(jobId)).Status;
    }

    private async Task<IReadOnlyList<Guid>> IdsHighestFirstAsync(PriorityKind kind)
    {
        using var scope = _fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<PriorityRepository>().GetAllAsync(kind)).Select(p => p.Id).ToList();
    }

    [Test]
    public async Task RaisingAWorkersPriorityAboveAJobsMinimum_ByReordering_DispatchesTheJob()
    {
        await ReadyWorkerAsync("reorder-low", StandardPriority.Low);
        var jobId = await EnqueueAsync(minWorkerPriority: StandardPriority.High);
        Assert.That(await StatusAsync(jobId), Is.EqualTo(JobStatus.Queued));

        // High, Medium, Low becomes Low, High, Medium: the worker now outranks the job's minimum.
        var ids = await IdsHighestFirstAsync(PriorityKind.Worker);
        await Scheduler.ChangePrioritiesAsync(async p =>
        {
            await p.ReorderAsync(PriorityKind.Worker, [ids[2], ids[0], ids[1]]);
            return true;
        });

        Assert.That(await StatusAsync(jobId), Is.EqualTo(JobStatus.Running));
    }

    [Test]
    public async Task ReorderingJobPriorities_ChangesWhichQueuedJobGoesFirst()
    {
        var low = await EnqueueAsync(priority: StandardPriority.Low);
        var high = await EnqueueAsync(priority: StandardPriority.High);
        var ids = await IdsHighestFirstAsync(PriorityKind.Job);
        await Scheduler.ChangePrioritiesAsync(async p =>
        {
            await p.ReorderAsync(PriorityKind.Job, ids.Reverse().ToList());
            return true;
        });

        await ReadyWorkerAsync("reorder-jobs", StandardPriority.Medium);

        Assert.That(await StatusAsync(low), Is.EqualTo(JobStatus.Running), "Low is now the highest job priority");
        Assert.That(await StatusAsync(high), Is.EqualTo(JobStatus.Queued));
    }

    [Test]
    public async Task DeletingAWorkersPriority_ForAHigherOne_DispatchesWhatItNowQualifiesFor()
    {
        await ReadyWorkerAsync("delete-low", StandardPriority.Low);
        var jobId = await EnqueueAsync(minWorkerPriority: StandardPriority.High);
        var low = await _fixture.PriorityIdAsync(PriorityKind.Worker, StandardPriority.Low);
        var high = await _fixture.PriorityIdAsync(PriorityKind.Worker, StandardPriority.High);

        await Scheduler.ChangePrioritiesAsync(p => p.DeleteAsync(low, high));

        Assert.That(await StatusAsync(jobId), Is.EqualTo(JobStatus.Running));
    }

    [Test]
    public async Task AJobWithNoMinimum_RunsOnTheLowestWorker()
    {
        await ReadyWorkerAsync("no-minimum", StandardPriority.Low);

        var jobId = await EnqueueAsync();

        Assert.That(await StatusAsync(jobId), Is.EqualTo(JobStatus.Running));
    }
}

/// <summary>Looking up what uses a priority: the counts on its list, and the lists of workers, pipelines and jobs narrowed to it.</summary>
[TestFixture]
[Category("Integration")]
public class PriorityLookupTests
{
    private IntegrationTestFixture _fixture = null!;
    private Guid _repositoryId;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new IntegrationTestFixture();
        await _fixture.SetupAsync();
        _repositoryId = (await _fixture.CreateRepositoryAsync(new RepositorySubmission { Name = "lookup-repo", RepoUrl = "https://example.invalid/repo.git" })).Id;
    }

    [TearDown]
    public async Task TearDown()
    {
        await _fixture.DisposeAsync();
    }

    private async Task<T> GetAsync<T>(string url)
    {
        return (await _fixture.HttpClient.GetFromJsonAsync<T>(url, JsonOptions))!;
    }

    private async Task<Guid> RegisterWorkerAsync(string name, string priority)
    {
        using var scope = _fixture.Services.CreateScope();
        var id = (await scope.ServiceProvider.GetRequiredService<WorkerRegistrationService>().RegisterWorkerAsync(name)).WorkerId;
        (await _fixture.HttpClient.PutAsJsonAsync($"/api/workers/{id}", new WorkerUpdate { Priority = priority })).EnsureSuccessStatusCode();
        return id;
    }

    private Task<PipelineInfo> PipelineAsync(string name, string? priority, string? minWorkerPriority)
    {
        return _fixture.CreatePipelineAsync(new PipelineSubmission
        {
            Name = name,
            RepositoryId = _repositoryId,
            Ref = "master",
            ScriptPath = "pipeline.csx",
            Priority = priority,
            MinWorkerPriority = minWorkerPriority
        });
    }

    private Task<Guid> SubmitAsync(string? priority, string? minWorkerPriority)
    {
        return _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = "https://example.invalid/repo.git",
            Ref = "master",
            ScriptPath = "pipeline.csx",
            Priority = priority,
            MinWorkerPriority = minWorkerPriority
        });
    }

    [Test]
    public async Task EachPriority_CountsWhatUsesIt()
    {
        await RegisterWorkerAsync("counted-high", StandardPriority.High);
        await RegisterWorkerAsync("counted-low", StandardPriority.Low);
        await PipelineAsync("counted-pipeline", StandardPriority.High, StandardPriority.High);
        await SubmitAsync(StandardPriority.High, StandardPriority.High);
        await SubmitAsync(StandardPriority.Low, minWorkerPriority: null);

        var workerPriorities = await GetAsync<List<PriorityInfo>>("/api/priorities/worker");
        var jobPriorities = await GetAsync<List<PriorityInfo>>("/api/priorities/job");

        var high = workerPriorities.Single(p => p.Name == StandardPriority.High).Usage;
        Assert.That((high.Workers, high.PipelinesNeedingIt, high.JobsNeedingIt), Is.EqualTo((1, 1, 1)));
        Assert.That(workerPriorities.Single(p => p.Name == StandardPriority.Medium).Usage, Is.EqualTo(new PriorityUsage()));
        var highJobs = jobPriorities.Single(p => p.Name == StandardPriority.High).Usage;
        Assert.That((highJobs.Pipelines, highJobs.Jobs), Is.EqualTo((1, 1)));
        Assert.That(jobPriorities.Single(p => p.Name == StandardPriority.Low).Usage.Jobs, Is.EqualTo(1));
    }

    [Test]
    public async Task Lists_NarrowToAPriority_ByName()
    {
        var high = await RegisterWorkerAsync("listed-high", StandardPriority.High);
        await RegisterWorkerAsync("listed-low", StandardPriority.Low);
        var needsHigh = await PipelineAsync("needs-high", priority: null, StandardPriority.High);
        var lowPipeline = await PipelineAsync("low-pipeline", StandardPriority.Low, minWorkerPriority: null);
        var highJob = await SubmitAsync(StandardPriority.High, minWorkerPriority: null);
        var needsHighJob = await SubmitAsync(priority: null, StandardPriority.High);

        var workers = await GetAsync<List<WorkerView>>("/api/workers?priority=high");
        var pipelinesNeeding = await GetAsync<List<PipelineInfo>>("/api/pipelines?minWorkerPriority=High");
        var lowPipelines = await GetAsync<List<PipelineInfo>>("/api/pipelines?priority=Low");
        var highJobs = await GetAsync<List<JobInfo>>("/api/jobs?priority=High");
        var jobsNeeding = await GetAsync<List<JobInfo>>("/api/jobs?minWorkerPriority=High");

        Assert.That(workers.Select(w => w.Id), Is.EqualTo(new[] { high }));
        Assert.That(pipelinesNeeding.Select(p => p.Id), Is.EqualTo(new[] { needsHigh.Id }));
        Assert.That(lowPipelines.Select(p => p.Id), Is.EqualTo(new[] { lowPipeline.Id }));
        Assert.That(highJobs.Select(j => j.Id), Is.EqualTo(new[] { highJob }));
        Assert.That(jobsNeeding.Select(j => j.Id), Is.EqualTo(new[] { needsHighJob }));
    }

    [TestCase("/api/workers?priority=Ultra")]
    [TestCase("/api/pipelines?priority=Ultra")]
    [TestCase("/api/pipelines?minWorkerPriority=Ultra")]
    [TestCase("/api/jobs?priority=Ultra")]
    [TestCase("/api/jobs?minWorkerPriority=Ultra")]
    public async Task NarrowingToAnUnknownPriority_IsABadRequest(string url)
    {
        var response = await _fixture.HttpClient.GetAsync(url);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }
}
