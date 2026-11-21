using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Diagnostics;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>What workers say about the free space on their disks, and what the server makes of it.</summary>
[TestFixture]
[Category("Integration")]
public class DiskSpaceTests
{
    private const long Gb = FakeDiskSpace.Gb;

    private IntegrationTestFixture _fixture = null!;
    private TestGitRepository _repo = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new IntegrationTestFixture { WorkerMinFreeBytes = 3 * Gb };
        await _fixture.SetupAsync();
        _repo = new TestGitRepository();
    }

    [TearDown]
    public async Task TearDown()
    {
        _repo.Dispose();
        await _fixture.DisposeAsync();
    }

    private async Task<WorkerView> ViewAsync(Guid workerId)
    {
        using var scope = _fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<WorkerRepository>().GetByIdAsync(workerId))!;
    }

    private async Task<WorkerView> WaitForFreeAsync(Guid workerId, long free)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var view = await ViewAsync(workerId);
            if (view.DiskSpace?.FreeBytes == free)
            {
                return view;
            }
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(30))
            {
                Assert.Fail($"the worker's free space never showed as {free}; last {view.DiskSpace?.FreeBytes}");
            }
            await Task.Delay(100);
        }
    }

    [Test]
    public async Task Connecting_ReportsFreeSpace_AndTheFloor()
    {
        var workerId = await _fixture.StartWorkerAsync();

        var view = await WaitForFreeAsync(workerId, _fixture.WorkerDisk.FreeBytes);

        Assert.That(view.DiskSpace!.TotalBytes, Is.EqualTo(_fixture.WorkerDisk.TotalBytes));
        Assert.That(view.DiskSpace.MinFreeBytes, Is.EqualTo(3 * Gb));
        Assert.That(view.LowOnDisk, Is.False);
    }

    [Test]
    public async Task AJobEnding_ReportsTheSpaceItLeft()
    {
        var workerId = await _fixture.StartWorkerAsync();
        _repo.AddFile("pipeline.csx", """
            Step("check")
                .Image("alpine:latest")
                .Run(async ctx => await ctx.Exec("echo", "Hello"));
            """);
        _repo.Commit("Add pipeline");

        // Read only when reported, so set before the job: the next report after the one at connecting goes with its outcome. Above the floor, which would stop the job.
        _fixture.WorkerDisk.FreeBytes = 4 * Gb;
        var jobId = await _fixture.SubmitJobAsync(new JobSubmission { RepoUrl = _repo.Url, Ref = "master", ScriptPath = "pipeline.csx" });
        var job = await _fixture.WaitForJobCompletionAsync(jobId);
        Assert.That(job.Status, Is.EqualTo(JobStatus.Success));

        // Reported with the outcome, not at the next half-minute report.
        await WaitForFreeAsync(workerId, 4 * Gb);
    }

    [Test]
    public void LowOnDisk_IsBelowTheFloor()
    {
        WorkerView With(long free)
        {
            return new WorkerView { Id = Guid.NewGuid(), Name = "w", Priority = new PriorityRef(Guid.NewGuid(), "p"), DiskSpace = new DiskSpaceReport { FreeBytes = free, TotalBytes = 100 * Gb, MinFreeBytes = 3 * Gb } };
        }

        Assert.That(With(2 * Gb).LowOnDisk, Is.True);
        Assert.That(With(3 * Gb).LowOnDisk, Is.False);
        Assert.That(new WorkerView { Id = Guid.NewGuid(), Name = "w", Priority = new PriorityRef(Guid.NewGuid(), "p") }.LowOnDisk, Is.False, "not known to be low before a report");
    }

    [Test]
    public void OtherUse_IsWhatTheWorkspacesAndMirrorsDontAccountFor()
    {
        var view = new WorkerView
        {
            Id = Guid.NewGuid(),
            Name = "w",
            Priority = new PriorityRef(Guid.NewGuid(), "p"),
            DiskSpace = new DiskSpaceReport { FreeBytes = 40 * Gb, TotalBytes = 100 * Gb, MinFreeBytes = Gb },
            UsageMeasured = true,
            MirrorsSizeBytes = 5 * Gb,
            Workspaces = [new WorkspaceView { Name = "a", Path = "/a", LastUsed = DateTimeOffset.UtcNow, Measured = true, SizeBytes = 20 * Gb }]
        };

        Assert.That(view.OtherUseBytes, Is.EqualTo(35 * Gb));
        Assert.That(new WorkerView { Id = view.Id, Name = "w", Priority = view.Priority, DiskSpace = view.DiskSpace }.OtherUseBytes, Is.Null, "nothing to say before the workspaces are measured");
    }
}
