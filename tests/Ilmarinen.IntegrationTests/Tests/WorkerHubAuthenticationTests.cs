using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Microsoft.AspNetCore.SignalR;
using NUnit.Framework;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// Every hub method a worker calls after the handshake answers only to an authenticated worker, and the ones that name a job only for that worker's own job. Driven through the hub directly, as anything that reaches the worker port could.
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
    public async Task JobStarted_FromAnUnauthenticatedConnection_IsRefused()
    {
        var jobId = await SubmitAsync();

        Assert.ThrowsAsync<HubException>(() => _fixture.CreateHub("conn-stranger").JobStarted(jobId));

        Assert.That((await _fixture.GetJobAsync(jobId)).Status, Is.EqualTo(JobStatus.Queued));
    }

    [Test]
    public async Task JobStarted_ForAnotherWorkersJob_LeavesItAlone()
    {
        var (_, owner) = await _fixture.CreateHubForRegisteredWorkerAsync("owner", "conn-owner");
        var (other, _) = await _fixture.CreateHubForRegisteredWorkerAsync("other", "conn-other");
        var jobId = await SubmitAsync();
        await _fixture.StartJobOnWorkerAsync(jobId, owner);
        var startedAt = (await _fixture.GetJobAsync(jobId)).StartedAt;

        await other.JobStarted(jobId);

        Assert.That((await _fixture.GetJobAsync(jobId)).StartedAt, Is.EqualTo(startedAt));
    }

    [Test]
    public async Task ReportCommit_FromAnUnauthenticatedConnection_IsRefused()
    {
        var jobId = await SubmitAsync();

        Assert.ThrowsAsync<HubException>(() => _fixture.CreateHub("conn-stranger").ReportCommit(jobId, "abc123"));

        Assert.That((await _fixture.GetJobAsync(jobId)).Commit, Is.Null);
    }

    [Test]
    public async Task ReportCommit_ForAnotherWorkersJob_LeavesItAlone()
    {
        var (_, owner) = await _fixture.CreateHubForRegisteredWorkerAsync("owner", "conn-owner");
        var (other, _) = await _fixture.CreateHubForRegisteredWorkerAsync("other", "conn-other");
        var jobId = await SubmitAsync();
        await _fixture.StartJobOnWorkerAsync(jobId, owner);

        await other.ReportCommit(jobId, "abc123");

        Assert.That((await _fixture.GetJobAsync(jobId)).Commit, Is.Null);
    }

    [Test]
    public async Task ReportCommit_ForItsOwnJob_IsRecorded()
    {
        var (hub, owner) = await _fixture.CreateHubForRegisteredWorkerAsync("owner", "conn-owner");
        var jobId = await SubmitAsync();
        await _fixture.StartJobOnWorkerAsync(jobId, owner);

        await hub.ReportCommit(jobId, "abc123");

        Assert.That((await _fixture.GetJobAsync(jobId)).Commit, Is.EqualTo("abc123"));
    }

    [Test]
    public async Task StreamLogs_FromAnUnauthenticatedConnection_IsRefused()
    {
        var jobId = await SubmitAsync();
        var chunk = new LogChunk { JobId = jobId, SequenceNumber = 1, Content = "{}", Timestamp = DateTime.UtcNow };

        Assert.ThrowsAsync<HubException>(() => _fixture.CreateHub("conn-stranger").StreamLogs(chunk));
    }

    [Test]
    public void WorkspaceDeleted_FromAnUnauthenticatedConnection_IsRefused()
    {
        var result = new DeleteWorkspaceResult { Status = DeleteWorkspaceStatus.Success };

        Assert.ThrowsAsync<HubException>(() => _fixture.CreateHub("conn-stranger").WorkspaceDeleted("ws", result));
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
