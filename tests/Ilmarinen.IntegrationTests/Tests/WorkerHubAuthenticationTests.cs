using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using NUnit.Framework;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// Every hub method a worker calls after the handshake answers only the worker's current connection, and the ones that name a job only for that worker's own job. Driven through the hub directly, as anything that reaches the worker port could.
/// </summary>
[TestFixture]
[Category("Integration")]
public class WorkerHubAuthenticationTests
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

    [Test]
    public async Task EveryCall_FromAnUnauthenticatedConnection_IsFenced()
    {
        var jobId = await SubmitAsync();
        var stranger = _fixture.CreateHub("conn-stranger");
        var state = new WorkerState { Seq = 1, Activity = WorkerActivity.Idle, DiagnosticStatus = DiagnosticStatus.Healthy };

        Assert.That((await stranger.Resume(new WorkerResume { State = state, Outcomes = [] })).Answer, Is.EqualTo(SessionAnswer.Fenced));
        Assert.That(await stranger.ReportState(state), Is.EqualTo(SessionAnswer.Fenced));
        Assert.That(await stranger.ReportOutcome(DirectedWorker.Completed(jobId, Guid.NewGuid(), JobStatus.Success)), Is.EqualTo(SessionAnswer.Fenced));
        Assert.That(await stranger.Heartbeat(), Is.EqualTo(SessionAnswer.Fenced));
        Assert.That(await stranger.ReportCommit(jobId, "abc123"), Is.EqualTo(SessionAnswer.Fenced));
        Assert.That(await stranger.StreamLogs(new LogChunk { JobId = jobId, SequenceNumber = 1, Content = "{}", Timestamp = DateTime.UtcNow }), Is.EqualTo(SessionAnswer.Fenced));
        Assert.That(await stranger.StreamMetrics(new JobMetricsChunk { JobId = jobId, SequenceNumber = 1, Content = "{}", Timestamp = DateTime.UtcNow }), Is.EqualTo(SessionAnswer.Fenced));
        Assert.That(stranger.WorkspaceDeleted("ws", new DeleteWorkspaceResult { Status = DeleteWorkspaceStatus.Success }), Is.EqualTo(SessionAnswer.Fenced));

        var job = await _fixture.GetJobAsync(jobId);
        Assert.That(job.Status, Is.EqualTo(JobStatus.Queued));
        Assert.That(job.Commit, Is.Null);
    }

    [Test]
    public async Task ReportCommit_ForAnotherWorkersJob_LeavesItAlone()
    {
        var owner = await DirectedWorker.RegisterAsync(_fixture, "owner");
        await owner.ConnectAsync("conn-owner");
        var other = await DirectedWorker.RegisterAsync(_fixture, "other");
        await other.ConnectAsync("conn-other");
        var jobId = await SubmitAsync();
        await _fixture.StartJobOnWorkerAsync(jobId, owner);

        await other.Hub.ReportCommit(jobId, "abc123");

        Assert.That((await _fixture.GetJobAsync(jobId)).Commit, Is.Null);
    }

    [Test]
    public async Task ReportCommit_ForItsOwnJob_IsRecorded()
    {
        var owner = await DirectedWorker.RegisterAsync(_fixture, "owner");
        await owner.ConnectAsync("conn-owner");
        var jobId = await SubmitAsync();
        await _fixture.StartJobOnWorkerAsync(jobId, owner);

        await owner.Hub.ReportCommit(jobId, "abc123");

        Assert.That((await _fixture.GetJobAsync(jobId)).Commit, Is.EqualTo("abc123"));
    }

    private Task<Guid> SubmitAsync()
    {
        return _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = "/nonexistent/repo",
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });
    }
}
