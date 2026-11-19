using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// The server's side of the worker protocol (spec/README.md) in the interleavings the model checks: outcomes on a connection that hasn't resumed, hand-backs, stale assignments, superseded process instances, and the order of what the server sends. Driven through the hub directly.
/// </summary>
[TestFixture]
[Category("Integration")]
public class WorkerProtocolTests
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

    // The queue drain began with a job result acted on before the reconnecting worker had said what it was running.
    [Test]
    public async Task Outcome_BeforeResume_IsRefusedAndDispatchesNothing()
    {
        var worker = await DirectedWorker.ReadyAsync(_fixture, "early-outcome-worker", "conn-early-outcome-1");
        var running = await SubmitAsync();
        var queued = await SubmitAsync();
        var assignment = (await worker.SentAsync()).Assignments.Single(a => a.Id == running);

        await worker.ConnectAsync("conn-early-outcome-2");
        var answer = await worker.ReportAsync(DirectedWorker.Completed(running, assignment.AssignmentId, JobStatus.Success));

        Assert.That(answer, Is.EqualTo(SessionAnswer.NotResumed));
        Assert.That((await _fixture.GetJobAsync(running)).Status, Is.EqualTo(JobStatus.Running));
        Assert.That((await _fixture.GetJobAsync(queued)).Status, Is.EqualTo(JobStatus.Queued));

        // The same outcome carried by Resume is applied, and only then is the worker handed the next job.
        await worker.ResumeAsync(worker.Idle(), DirectedWorker.Completed(running, assignment.AssignmentId, JobStatus.Success));
        Assert.That((await _fixture.GetJobAsync(running)).Status, Is.EqualTo(JobStatus.Success));
        Assert.That((await worker.SentAsync()).Assignments.Select(a => a.Id), Is.EqualTo(new[] { queued }));
    }

    // A worker that can't take an assignment gives it back; the job is someone else's to run, never a failure.
    [Test]
    public async Task HandBack_QueuesTheJobAgainForAnotherWorker()
    {
        var busy = await DirectedWorker.ReadyAsync(_fixture, "busy-worker", "conn-busy");
        var jobId = await SubmitAsync();
        var assignment = (await busy.SentAsync()).Assignments.Single(a => a.Id == jobId);
        await busy.ReportAsync(busy.Diagnosing());
        var other = await DirectedWorker.ReadyAsync(_fixture, "other-worker", "conn-other");

        await busy.ReportAsync(DirectedWorker.HandBack(jobId, assignment.AssignmentId));

        Assert.That((await other.SentAsync()).Assignments.Select(a => a.Id), Is.EqualTo(new[] { jobId }));
        var job = await _fixture.GetJobAsync(jobId);
        Assert.That(job.Status, Is.EqualTo(JobStatus.Running));
        Assert.That(job.WorkerId, Is.EqualTo(other.WorkerId));
    }

    // A hand-back delivered late — after the job was requeued and assigned again — names an assignment that is over.
    [Test]
    public async Task HandBack_ForAnEarlierAssignment_LeavesTheCurrentOneAlone()
    {
        var worker = await DirectedWorker.ReadyAsync(_fixture, "late-handback-worker", "conn-late-handback");
        var jobId = await SubmitAsync();
        var first = (await worker.SentAsync()).Assignments.Single(a => a.Id == jobId);
        await worker.ReportAsync(DirectedWorker.HandBack(jobId, first.AssignmentId));
        var second = (await worker.SentAsync()).Assignments.Last(a => a.Id == jobId);
        Assert.That(second.AssignmentId, Is.Not.EqualTo(first.AssignmentId), "handed out again, under a new assignment");

        await worker.ReportAsync(DirectedWorker.HandBack(jobId, first.AssignmentId));
        await worker.ReportAsync(DirectedWorker.Completed(jobId, first.AssignmentId, JobStatus.Failed));

        var job = await _fixture.GetJobAsync(jobId);
        Assert.That(job.Status, Is.EqualTo(JobStatus.Running), "neither the stale hand-back nor a stale outcome touches the current assignment");
        Assert.That((await worker.SentAsync()).Assignments.Count(a => a.Id == jobId), Is.EqualTo(2));
    }

    // Once a new process of a worker has the session, the old one never gets it back, however high its counter: two processes on one key can't take turns.
    [Test]
    public async Task NewInstance_RetiresTheOldOne()
    {
        var worker = await DirectedWorker.RegisterAsync(_fixture, "restarted-worker");
        await worker.ConnectAsync("conn-old-1");
        var oldHub = worker.Hub;
        var oldId = worker.InstanceId;

        worker.Restart();
        Assert.That(await worker.ConnectAsync("conn-new-1"), Is.EqualTo(SessionAnswer.Ok));
        var newHub = worker.Hub;
        Assert.That(await oldHub.Heartbeat(), Is.EqualTo(SessionAnswer.Fenced));

        var stale = await ReplayInstanceAsync(worker, oldId, "conn-old-9", counter: 9);
        Assert.That(stale, Is.EqualTo(SessionAnswer.Fenced));
        Assert.That(await newHub.Heartbeat(), Is.EqualTo(SessionAnswer.Ok), "the new process keeps its session");
    }

    // A cancel's message must not overtake the assignment it cancels: a worker told to stop a job it hasn't been given yet ignores it, then runs the job.
    [Test]
    public async Task Cancel_RightAfterAssignment_ReachesTheWorkerAfterTheAssignment()
    {
        _fixture.WorkerClients.AssignmentDelay = TimeSpan.FromMilliseconds(500);
        var worker = await DirectedWorker.ReadyAsync(_fixture, "cancel-order-worker", "conn-cancel-order");
        var jobId = await SubmitAsync();

        Assert.That(await _fixture.CancelJobAsync(jobId), Is.True);

        Assert.That((await worker.SentAsync()).Messages, Is.EqualTo(new[] { $"AssignJob {jobId}", $"CancelJob {jobId}" }));
    }

    [Test]
    public async Task Cancel_OfARunningJob_TellsItsWorker()
    {
        var worker = await DirectedWorker.ReadyAsync(_fixture, "cancel-worker", "conn-cancel");
        var jobId = await SubmitAsync();

        await _fixture.CancelJobAsync(jobId);

        Assert.That((await worker.SentAsync()).CancelledJobs, Is.EqualTo(new[] { jobId.ToString() }));
    }

    // Cancelling the job a worker is held for can free it, so the cancel dispatches. The worker here never got its assignment (the send failed), so nothing it says would.
    [Test]
    public async Task Cancel_FreeingAWorker_DispatchesTheNextJob()
    {
        var worker = await DirectedWorker.ReadyAsync(_fixture, "cancel-frees-worker", "conn-cancel-frees");
        var stuck = await SubmitAsync();
        var next = await SubmitAsync();
        await worker.ReportAsync(worker.Idle());
        Assert.That((await _fixture.GetJobAsync(next)).Status, Is.EqualTo(JobStatus.Queued), "held for the job the server thinks it is running");

        await _fixture.CancelJobAsync(stuck);

        Assert.That((await worker.SentAsync()).Assignments.Select(a => a.Id), Is.EqualTo(new[] { stuck, next }));
    }

    // A cancel made while the worker was away stands when the worker comes back as a new process, with a single notification. (The race where the cancel lands while that Resume is reconciling is closed by the worker's lock, which both take.)
    [Test]
    public async Task Cancel_ThenTheWorkerReturnsAsANewProcess_TheJobStaysCancelled()
    {
        var subscriber = await _fixture.RegisterSubscriberAsync(new SubscriberRegistration { Name = "cancel-stands" });
        var worker = await DirectedWorker.ReadyAsync(_fixture, "cancel-stands-worker", "conn-cancel-stands-1");
        var jobId = await SubmitAsync();
        await worker.Hub.OnDisconnectedAsync(null);
        await _fixture.CancelJobAsync(jobId);

        worker.Restart();
        await worker.ConnectAsync("conn-cancel-stands-2");
        await worker.ResumeAsync(worker.Idle());

        Assert.That((await _fixture.GetJobAsync(jobId)).Status, Is.EqualTo(JobStatus.Cancelled));
        Assert.That((await _fixture.PullNotificationsAsync(subscriber.Id)).Count(n => n.JobId == jobId), Is.EqualTo(1), "one terminal transition, one notification");
    }

    /// <summary>A handshake as an earlier process instance of the worker would make it, with whatever counter.</summary>
    private static async Task<SessionAnswer> ReplayInstanceAsync(DirectedWorker worker, Guid instanceId, string connectionId, long counter)
    {
        var current = worker.InstanceId;
        worker.UseInstance(instanceId);
        try
        {
            return await worker.ConnectAsCounterAsync(connectionId, counter);
        }
        finally
        {
            worker.UseInstance(current);
        }
    }

    private Task<Guid> SubmitAsync()
    {
        return _fixture.Services.GetRequiredService<JobScheduler>().EnqueueJobAsync(new JobSubmission
        {
            RepoUrl = "/nonexistent/repo",
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });
    }
}
