using Ilmarinen.Database;
using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.NotificationClient;
using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Services.Wake;
using Ilmarinen.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>The server waking sleeping workers for the jobs that need them, giving up on those that don't come, and the stuck-job report counting on them only while they might.</summary>
[TestFixture]
[Category("Integration")]
public class WorkerWakeTests
{
    private static readonly TimeSpan Asleep = TimeSpan.FromMinutes(2);

    private IntegrationTestFixture _fixture = null!;
    private ManualTime _time = null!;

    [SetUp]
    public async Task SetUp()
    {
        _time = new ManualTime();
        _fixture = new IntegrationTestFixture { ServerTime = _time };
        await _fixture.SetupAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _fixture.DisposeAsync();
    }

    private T Get<T>() where T : notnull
    {
        return _fixture.Services.GetRequiredService<T>();
    }

    private static JobSubmission Submission(string minWorkerPriority = StandardPriority.Low)
    {
        return new JobSubmission
        {
            RepoUrl = "https://example.invalid/repo.git",
            Ref = "master",
            ScriptPath = "pipeline.csx",
            GitTokenMode = GitTokenMode.None,
            MinWorkerPriority = minWorkerPriority
        };
    }

    private async Task<DirectedWorker> SleeperAsync(string name, string mac, string priority = StandardPriority.Medium)
    {
        var worker = await DirectedWorker.RegisterAsync(_fixture, name);
        await Get<WorkerRepository>().SetPriorityAsync(worker.WorkerId, await _fixture.PriorityIdAsync(PriorityKind.Worker, priority));
        await Get<WorkerRepository>().SetWakeMacAddressAsync(worker.WorkerId, mac);
        return worker;
    }

    /// <summary>A job queued while every worker is asleep, then enough time for waking to act on it.</summary>
    private async Task<Guid> QueueWhileAsleepAsync(string minWorkerPriority = StandardPriority.Low)
    {
        var jobId = await Get<JobScheduler>().EnqueueJobAsync(Submission(minWorkerPriority));
        _time.Advance(Asleep);
        return jobId;
    }

    private Task TickAsync()
    {
        return Get<WorkerWaker>().TickAsync(CancellationToken.None);
    }

    private async Task<JobStatus> JobStatusAsync(Guid jobId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        return (await db.Jobs.FirstAsync(j => j.Id == jobId)).Status;
    }

    private async Task<UnsatisfiableJobsReport> StuckAsync()
    {
        using var client = new IlmarinenNotificationClient(_fixture.HttpClient.BaseAddress!.ToString(), _fixture.HttpClient);
        return await client.GetUnsatisfiableJobsAsync(CancellationToken.None);
    }

    [Test]
    public async Task NoSleeper_IsWokenForAJobQueuedWhileTheFleetDrains()
    {
        await SleeperAsync("wake-drain", "aa:bb:cc:dd:ee:0d");
        await Get<JobScheduler>().SetDrainingAsync(true);
        await QueueWhileAsleepAsync();

        await TickAsync();
        Assert.That(Get<RecordingWakeSender>().Sent, Is.Empty, "nothing is dispatched while draining, so nothing needs waking");

        await Get<JobScheduler>().SetDrainingAsync(false);
        await TickAsync();
        Assert.That(Get<RecordingWakeSender>().Sent, Is.EqualTo(new[] { "aa:bb:cc:dd:ee:0d" }));
    }

    [Test]
    public async Task ASleeper_IsWokenForAQueuedJob_AndTakesItOnceBack()
    {
        var worker = await SleeperAsync("wake-one", "aa:bb:cc:dd:ee:01");
        var jobId = await QueueWhileAsleepAsync();

        await TickAsync();

        Assert.That(Get<RecordingWakeSender>().Sent, Is.EqualTo(new[] { "aa:bb:cc:dd:ee:01" }));
        Assert.That(Get<WakeStatus>().WaitingFor(jobId)?.Id, Is.EqualTo(worker.WorkerId));
        Assert.That((await Get<WorkerRepository>().GetByIdAsync(worker.WorkerId))!.IsWaking, Is.True);
        Assert.That((await StuckAsync()).Jobs, Is.Empty, "a worker is on its way");

        await worker.ConnectAsync("conn-wake-one");
        await worker.ResumeAsync(worker.Idle());
        Assert.That(await JobStatusAsync(jobId), Is.EqualTo(JobStatus.Running), "dispatch, not waking, hands it the job");

        await TickAsync();
        Assert.That(Get<WakeStatus>().IsWaking(worker.WorkerId), Is.False);
    }

    [Test]
    public async Task TwoJobs_WakeTwoSleepers_AndNotOneTwice()
    {
        await SleeperAsync("wake-two-a", "aa:bb:cc:dd:ee:0a");
        await SleeperAsync("wake-two-b", "aa:bb:cc:dd:ee:0b");
        await Get<JobScheduler>().EnqueueJobAsync(Submission());
        await QueueWhileAsleepAsync();

        await TickAsync();
        Get<RecordingWakeSender>().Clear();
        await TickAsync();

        Assert.That(Get<RecordingWakeSender>().Sent, Is.EquivalentTo(new[] { "aa:bb:cc:dd:ee:0a", "aa:bb:cc:dd:ee:0b" }), "each resent once, and nobody else");
    }

    [Test]
    public async Task NoSleeperIsWoken_ThatIsPaused_HasNoAddress_OrCantTakeTheJob_OrWithWakingOff()
    {
        var paused = await SleeperAsync("wake-paused", "aa:bb:cc:dd:ee:02", StandardPriority.High);
        await Get<JobScheduler>().SetWorkerPausedAsync(paused.WorkerId, true);
        await DirectedWorker.RegisterAsync(_fixture, "wake-no-address");
        await SleeperAsync("wake-low", "aa:bb:cc:dd:ee:03", StandardPriority.Low);
        await QueueWhileAsleepAsync(StandardPriority.High);

        await TickAsync();
        Assert.That(Get<RecordingWakeSender>().Sent, Is.Empty);

        await SleeperAsync("wake-off", "aa:bb:cc:dd:ee:04", StandardPriority.High);
        Get<RecordingWakeSender>().Enabled = false;
        await TickAsync();
        Assert.That(Get<RecordingWakeSender>().Sent, Is.Empty);
    }

    [Test]
    public async Task ASleeperThatDoesntWake_IsGivenUpOn_TheJobStaysQueued_AndTheNextIsWoken()
    {
        var timeout = _fixture.Services.GetRequiredService<Ilmarinen.Server.ServerConfig>().WakeTimeout;
        var first = await SleeperAsync("wake-dead", "aa:bb:cc:dd:ee:05", StandardPriority.High);
        var second = await SleeperAsync("wake-spare", "aa:bb:cc:dd:ee:06", StandardPriority.Low);
        var jobId = await QueueWhileAsleepAsync();
        await TickAsync();
        Assert.That(Get<WakeStatus>().WaitingFor(jobId)?.Id, Is.EqualTo(first.WorkerId), "the higher priority first");

        _time.Advance(timeout);
        Get<RecordingWakeSender>().Clear();
        await TickAsync();

        Assert.That(await JobStatusAsync(jobId), Is.EqualTo(JobStatus.Queued));
        Assert.That(Get<WakeStatus>().FailedAt(first.WorkerId), Is.Not.Null);
        Assert.That(Get<WakeStatus>().WaitingFor(jobId)?.Id, Is.EqualTo(second.WorkerId));
        Assert.That(Get<RecordingWakeSender>().Sent, Is.EqualTo(new[] { "aa:bb:cc:dd:ee:06" }));
        Assert.That((await Get<WorkerRepository>().GetByIdAsync(first.WorkerId))!.WakeFailedAt, Is.Not.Null);
    }

    [Test]
    public async Task TheStuckReport_CountsOnASleeperUntilItFailsToWake_AndDoesntForgiveItWhenTheBackoffEnds()
    {
        var timeout = _fixture.Services.GetRequiredService<Ilmarinen.Server.ServerConfig>().WakeTimeout;
        var backoff = _fixture.Services.GetRequiredService<Ilmarinen.Server.ServerConfig>().WakeBackoff;
        await SleeperAsync("wake-report", "aa:bb:cc:dd:ee:07");
        var jobId = await QueueWhileAsleepAsync();

        Assert.That((await StuckAsync()).Jobs, Is.Empty, "it can still be woken");
        await TickAsync();
        _time.Advance(timeout);
        await TickAsync();

        var failed = await StuckAsync();
        Assert.That(failed.Jobs.Select(j => j.Id), Is.EqualTo(new[] { jobId }));
        Assert.That(failed.UnreachableWorkers, Is.EqualTo(new[] { "wake-report" }));

        _time.Advance(backoff);
        await TickAsync();
        Assert.That(Get<WakeStatus>().IsWaking((await Get<WorkerRepository>().GetAllAsync()).Single().Id), Is.True, "tried again");
        Assert.That((await StuckAsync()).Jobs.Select(j => j.Id), Is.EqualTo(new[] { jobId }), "but not counted on again until it has woken");
    }

    [Test]
    public async Task AWorkerThatWakesButIsInUse_IsLetBe_WithoutABackoff()
    {
        var timeout = _fixture.Services.GetRequiredService<Ilmarinen.Server.ServerConfig>().WakeTimeout;
        var worker = await SleeperAsync("wake-in-use", "aa:bb:cc:dd:ee:08");
        await QueueWhileAsleepAsync();
        await TickAsync();

        await worker.ConnectAsync("conn-wake-in-use");
        await worker.ResumeAsync(worker.Idle() with { Hold = WorkerHold.InUse });
        _time.Advance(timeout);
        await TickAsync();

        Assert.That(Get<WakeStatus>().IsWaking(worker.WorkerId), Is.False);
        Assert.That(Get<WakeStatus>().FailedAt(worker.WorkerId), Is.Null);
        Assert.That((await StuckAsync()).InUseWorkers, Is.EqualTo(new[] { "wake-in-use" }));
    }

    [Test]
    public async Task Wake_StartsAWake_AndForgetsTheFailure()
    {
        var timeout = _fixture.Services.GetRequiredService<Ilmarinen.Server.ServerConfig>().WakeTimeout;
        var worker = await SleeperAsync("wake-button", "aa:bb:cc:dd:ee:09");
        await QueueWhileAsleepAsync();
        await TickAsync();
        _time.Advance(timeout);
        await TickAsync();
        Assert.That(Get<WakeStatus>().FailedAt(worker.WorkerId), Is.Not.Null);

        Assert.That(await Get<WorkerWaker>().WakeNowAsync(worker.WorkerId, CancellationToken.None), Is.EqualTo(WakeRequestResult.Sent));

        Assert.That(Get<WakeStatus>().FailedAt(worker.WorkerId), Is.Null);
        Assert.That(Get<WakeStatus>().IsWaking(worker.WorkerId), Is.True);
    }

    [Test]
    public async Task AWorkerThatComesBackByItself_IsForgivenItsFailure()
    {
        var timeout = _fixture.Services.GetRequiredService<Ilmarinen.Server.ServerConfig>().WakeTimeout;
        var worker = await SleeperAsync("wake-self", "aa:bb:cc:dd:ee:0c");
        await QueueWhileAsleepAsync();
        await TickAsync();
        _time.Advance(timeout);
        await TickAsync();

        await worker.ConnectAsync("conn-wake-self");
        await worker.ResumeAsync(worker.Idle());
        await TickAsync();

        Assert.That(Get<WakeStatus>().FailedAt(worker.WorkerId), Is.Null);
        
    }
}
