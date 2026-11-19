using Ilmarinen.Database;
using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>Which queued job a worker is handed next: the highest priority it qualifies for, oldest first among equals. Nothing running is ever displaced.</summary>
[TestFixture]
[Category("Integration")]
public class JobPriorityDispatchTests
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

    private static JobSubmission Submission(JobPriority priority, WorkerPriority minWorkerPriority = WorkerPriority.Low, string script = "pipeline.csx")
    {
        return new JobSubmission
        {
            RepoUrl = "https://example.invalid/repo.git",
            Ref = "master",
            ScriptPath = script,
            GitTokenMode = GitTokenMode.None,
            Priority = priority,
            MinWorkerPriority = minWorkerPriority
        };
    }

    /// <summary>Queued behind the scheduler's back, so no dispatch sees it until the test asks for one.</summary>
    private async Task<Guid> QueueQuietlyAsync(JobPriority priority, WorkerPriority minWorkerPriority = WorkerPriority.Low, string script = "pipeline.csx")
    {
        using var scope = _fixture.Services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
        var id = (await jobs.CreateAsync(Submission(priority, minWorkerPriority, script))).Id;
        await jobs.UpdateStatusAsync(id, JobStatus.Queued);
        return id;
    }

    private async Task<DirectedWorker> ReadyWorkerAsync(string connectionId, WorkerPriority priority = WorkerPriority.Medium)
    {
        var worker = await DirectedWorker.RegisterAsync(_fixture, $"jp-{connectionId}");
        await _fixture.Services.GetRequiredService<WorkerRepository>().SetPriorityAsync(worker.WorkerId, priority);
        await worker.ConnectAsync(connectionId);
        await worker.ResumeAsync(worker.Idle());
        return worker;
    }

    private async Task<(JobStatus Status, Guid? WorkerId)> JobAsync(Guid jobId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        var job = await db.Jobs.FirstAsync(j => j.Id == jobId);
        return (job.Status, job.WorkerId);
    }

    /// <summary>The worker finishes the job it was last handed and reports itself idle, which dispatches.</summary>
    private static async Task<Guid> FinishLastAsync(DirectedWorker worker)
    {
        var assignment = (await worker.SentAsync()).Assignments.Last();
        await worker.ReportAsync(DirectedWorker.Completed(assignment.Id, assignment.AssignmentId, JobStatus.Success));
        await worker.ReportAsync(worker.Idle());
        return assignment.Id;
    }

    [Test]
    public async Task OneWorker_TakesTheQueueHighestPriorityFirst()
    {
        var worker = await ReadyWorkerAsync("conn-jp-order");
        var low = await QueueQuietlyAsync(JobPriority.Low);
        var medium = await QueueQuietlyAsync(JobPriority.Medium);
        var high = await QueueQuietlyAsync(JobPriority.High);

        await Scheduler.DispatchAsync();
        var first = await FinishLastAsync(worker);
        var second = await FinishLastAsync(worker);
        var third = await FinishLastAsync(worker);

        Assert.That(new[] { first, second, third }, Is.EqualTo(new[] { high, medium, low }), "the Low job is oldest, and still goes last");
    }

    [Test]
    public async Task EqualPriorities_GoOldestFirst()
    {
        var worker = await ReadyWorkerAsync("conn-jp-ties");
        var older = await QueueQuietlyAsync(JobPriority.High);
        var newer = await QueueQuietlyAsync(JobPriority.High);

        await Scheduler.DispatchAsync();
        var first = await FinishLastAsync(worker);
        var second = await FinishLastAsync(worker);

        Assert.That(new[] { first, second }, Is.EqualTo(new[] { older, newer }));
    }

    [Test]
    public async Task TwoReadyWorkers_TheHigherWorkerTakesTheHighestJob_TheOtherTheNext()
    {
        var highWorker = await ReadyWorkerAsync("conn-jp-two-high", WorkerPriority.High);
        var lowWorker = await ReadyWorkerAsync("conn-jp-two-low", WorkerPriority.Low);
        var low = await QueueQuietlyAsync(JobPriority.Low);
        var medium = await QueueQuietlyAsync(JobPriority.Medium);
        var high = await QueueQuietlyAsync(JobPriority.High);

        await Scheduler.DispatchAsync();

        Assert.That(await JobAsync(high), Is.EqualTo((JobStatus.Running, (Guid?)highWorker.WorkerId)));
        Assert.That(await JobAsync(medium), Is.EqualTo((JobStatus.Running, (Guid?)lowWorker.WorkerId)));
        Assert.That((await JobAsync(low)).Status, Is.EqualTo(JobStatus.Queued));
    }

    // A known limitation, recorded rather than wanted: matching is greedy, so the High worker, visited first, takes the High job any worker could have run, and the job only it can run waits although both could have started. Smarter matching should change this test, not preserve it.
    [Test]
    public async Task GreedyMatchingLimitation_AJobOnlyTheHighWorkerCanRunWaitsBehindOneAnyWorkerCould()
    {
        var highWorker = await ReadyWorkerAsync("conn-jp-greedy-high", WorkerPriority.High);
        var lowWorker = await ReadyWorkerAsync("conn-jp-greedy-low", WorkerPriority.Low);
        var anyWorker = await QueueQuietlyAsync(JobPriority.High);
        var highOnly = await QueueQuietlyAsync(JobPriority.Medium, WorkerPriority.High);

        await Scheduler.DispatchAsync();

        Assert.That(await JobAsync(anyWorker), Is.EqualTo((JobStatus.Running, (Guid?)highWorker.WorkerId)));
        Assert.That((await JobAsync(highOnly)).Status, Is.EqualTo(JobStatus.Queued));
        Assert.That((await lowWorker.SentAsync()).Assignments, Is.Empty);
    }

    [Test]
    public async Task AJobNoReadyWorkerQualifiesFor_WaitsWhileLowerPriorityWorkGoesAhead()
    {
        var worker = await ReadyWorkerAsync("conn-jp-qualify", WorkerPriority.Medium);
        var highNeedsHigh = await QueueQuietlyAsync(JobPriority.High, WorkerPriority.High);
        var medium = await QueueQuietlyAsync(JobPriority.Medium);

        await Scheduler.DispatchAsync();

        Assert.That((await JobAsync(highNeedsHigh)).Status, Is.EqualTo(JobStatus.Queued));
        Assert.That(await JobAsync(medium), Is.EqualTo((JobStatus.Running, (Guid?)worker.WorkerId)));
    }

    [Test]
    public async Task AHighJob_NeverDisplacesARunningLowOne()
    {
        var worker = await ReadyWorkerAsync("conn-jp-no-preempt");
        var low = await Scheduler.EnqueueJobAsync(Submission(JobPriority.Low));
        var high = await Scheduler.EnqueueJobAsync(Submission(JobPriority.High));

        Assert.That(await JobAsync(low), Is.EqualTo((JobStatus.Running, (Guid?)worker.WorkerId)));
        Assert.That((await JobAsync(high)).Status, Is.EqualTo(JobStatus.Queued));
        Assert.That((await worker.SentAsync()).CancelledJobs, Is.Empty);
    }

    [Test]
    public async Task AHandedBackJob_KeepsItsPlaceAheadOfAnOlderLowerOne()
    {
        var worker = await ReadyWorkerAsync("conn-jp-hand-back");
        await QueueQuietlyAsync(JobPriority.Medium);
        var high = await QueueQuietlyAsync(JobPriority.High);

        await Scheduler.DispatchAsync();
        var assignment = (await worker.SentAsync()).Assignments.Single();
        Assert.That(assignment.Id, Is.EqualTo(high));

        await worker.ReportAsync(DirectedWorker.HandBack(assignment.Id, assignment.AssignmentId));
        await worker.ReportAsync(worker.Idle());

        var sent = (await worker.SentAsync()).Assignments;
        Assert.That(sent.Select(a => a.Id), Is.EqualTo(new[] { high, high }), "requeued, it is still the first job in line");
    }

    // The queue forecast simulates dispatch over the queue as this lists it, so it must list it in dispatch order.
    [Test]
    public async Task TheUnfinishedJobs_AreListedInDispatchOrder()
    {
        await QueueQuietlyAsync(JobPriority.Low, script: "low.csx");
        await QueueQuietlyAsync(JobPriority.High, script: "high.csx");
        await QueueQuietlyAsync(JobPriority.Medium, script: "medium.csx");
        await QueueQuietlyAsync(JobPriority.High, script: "later-high.csx");

        using var scope = _fixture.Services.CreateScope();
        var unfinished = await scope.ServiceProvider.GetRequiredService<JobRepository>().GetUnfinishedJobsAsync();

        Assert.That(unfinished.Select(j => j.ScriptPath), Is.EqualTo(new[] { "high.csx", "later-high.csx", "medium.csx", "low.csx" }));
    }
}
