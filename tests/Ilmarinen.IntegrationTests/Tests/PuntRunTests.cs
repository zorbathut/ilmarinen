using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>A real worker's run punted while it runs: stopped, run again on the same worker, and finished, with one log telling the whole story.</summary>
[TestFixture]
[Category("Integration")]
public class PuntRunTests
{
    private IntegrationTestFixture _fixture = null!;
    private TestGitRepository _repo = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new IntegrationTestFixture();
        await _fixture.SetupAsync();
        await _fixture.StartWorkerAsync();

        _repo = new TestGitRepository();
    }

    [TearDown]
    public async Task TearDown()
    {
        _repo.Dispose();
        await _fixture.DisposeAsync();
    }

    [Test]
    public async Task APuntedRun_StopsAndRunsAgain_OnTheSameWorker_UntouchedByTheFirstRunsStop()
    {
        // Each run counts itself in the persistent workspace, then waits for the gate the test opens.
        _repo.AddFile("pipeline.csx", """
            Workspace("punt-ws");
            Step("count-and-wait")
                .Image("alpine:latest")
                .Run(async ctx => await ctx.Shell("n=$(( $(cat /workspace/.runs 2>/dev/null || echo 0) + 1 )); echo $n > /workspace/.runs; echo \"this is run $n\"; while [ ! -f /workspace/.gate ]; do sleep 0.1; done"));
            """);
        _repo.Commit("Add a gated pipeline that counts its runs");

        var jobId = await _fixture.SubmitJobAsync(new JobSubmission { RepoUrl = _repo.Url, Ref = "master", ScriptPath = "pipeline.csx" });
        var runs = Path.Combine(_fixture.WorkerWorkspacePath!, "punt-ws", ".runs");
        var gate = Path.Combine(_fixture.WorkerWorkspacePath!, "punt-ws", ".gate");
        try
        {
            await WaitUntilAsync(() => Task.FromResult(File.Exists(runs) && File.ReadAllText(runs).Trim() == "1"), "the first run to start");

            var startedAt = (await _fixture.GetJobAsync(jobId)).StartedAt!.Value;
            var answer = await _fixture.Services.GetRequiredService<JobScheduler>().PuntJobAsync(jobId, startedAt, force: true, "a test");
            Assert.That(answer!.Status, Is.EqualTo(PuntStatus.Requested));

            await WaitUntilAsync(() => Task.FromResult(File.Exists(runs) && File.ReadAllText(runs).Trim() == "2"), "the second run to start");
        }
        finally
        {
            // Opened whatever happened above, so a failure can't leave the job, and the fixture's teardown, waiting forever.
            File.WriteAllText(gate, "");
        }

        var job = await _fixture.WaitForJobCompletionAsync(jobId);
        Assert.That(job.Status, Is.EqualTo(JobStatus.Success), "the rerun isn't stopped by anything left of the first run's stop");

        var log = await _fixture.GetJobLogsAsync(jobId);
        var first = log.IndexOf("this is run 1", StringComparison.Ordinal);
        var punted = log.IndexOf("[ilmarinen]", StringComparison.Ordinal);
        var second = log.IndexOf("this is run 2", StringComparison.Ordinal);
        Assert.That(first, Is.GreaterThanOrEqualTo(0), log);
        Assert.That(punted, Is.GreaterThan(first), log);
        Assert.That(second, Is.GreaterThan(punted), log);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (!await condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(90))
            {
                Assert.Fail($"Timed out waiting for {what}");
            }
            await Task.Delay(100);
        }
    }
}
