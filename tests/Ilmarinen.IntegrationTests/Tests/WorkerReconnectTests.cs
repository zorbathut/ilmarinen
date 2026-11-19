using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using NUnit.Framework;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// A real worker losing its connection while the server keeps all its state — the shape of the 2026-11-18 queue drain, where a worker reconnected mid-job and the jobs queued behind it were failed in milliseconds while it ran one the server had stopped tracking.
/// </summary>
[TestFixture]
[Category("Integration")]
public class WorkerReconnectTests
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

    // The job finishes while the worker is back on a transport but not yet through its handshake, so its result has to wait for the re-sync — and that result is what frees the worker for the job queued behind it, in the middle of the re-sync.
    [Test]
    public async Task ConnectionDropsMidJob_JobFinishesBeforeTheWorkerResyncs_BothItAndTheQueuedJobSucceed()
    {
        var workerId = await _fixture.StartWorkerAsync();

        using var slowRepo = Pipeline("sleep 6");
        var slow = await _fixture.SubmitJobAsync(Submission(slowRepo));
        await WaitForStatusAsync(slow, JobStatus.Running);
        using var quickRepo = Pipeline("echo next");
        var queued = await _fixture.SubmitJobAsync(Submission(quickRepo));

        // The assignment the result frees the worker for arrives after the worker has looked at what it is running — one of the orderings a real network can produce, made certain here.
        _fixture.WorkerClients.AssignmentDelay = TimeSpan.FromSeconds(2);
        using (_fixture.Connections.HoldHandshakes())
        {
            _fixture.DropWorkerConnection(workerId);
            await Task.Delay(TimeSpan.FromSeconds(10));
        }

        var slowResult = await _fixture.WaitForJobCompletionAsync(slow);
        var queuedResult = await _fixture.WaitForJobCompletionAsync(queued);
        Assert.That(slowResult.Status, Is.EqualTo(JobStatus.Success), "the job that finished during the outage");
        Assert.That(queuedResult.Status, Is.EqualTo(JobStatus.Success), "the job queued behind it");
    }

    private static TestGitRepository Pipeline(string command)
    {
        var repo = new TestGitRepository();
        repo.AddFile("pipeline.csx", $$"""
            Step("work")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Shell("{{command}}");
                });
            """);
        repo.Commit("Add pipeline");
        return repo;
    }

    private static JobSubmission Submission(TestGitRepository repo)
    {
        return new JobSubmission { RepoUrl = repo.Url, Ref = "master", ScriptPath = "pipeline.csx" };
    }

    private async Task WaitForStatusAsync(Guid jobId, JobStatus status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while ((await _fixture.GetJobAsync(jobId)).Status != status)
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Job {jobId} never reached {status}");
            }
            await Task.Delay(100);
        }
    }
}
