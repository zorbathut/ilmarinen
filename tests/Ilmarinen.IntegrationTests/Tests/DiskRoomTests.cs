using Ilmarinen.Database.Entities;
using Ilmarinen.Database;
using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>Jobs go only to workers with room for them, learned from their scripts' latest runs; a worker makes that room by deleting its least recently used workspaces, or its own copy of the job's workspace when only a fresh start fits.</summary>
[TestFixture]
[Category("Integration")]
public class DiskRoomTests
{
    private const long Gb = FakeDiskSpace.Gb;

    private IntegrationTestFixture _fixture = null!;
    private TestGitRepository _repo = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new IntegrationTestFixture();
        await _fixture.SetupAsync();
        _repo = new TestGitRepository();
    }

    [TearDown]
    public async Task TearDown()
    {
        _repo.Dispose();
        await _fixture.DisposeAsync();
    }

    private JobScheduler Scheduler
    {
        get { return _fixture.Services.GetRequiredService<JobScheduler>(); }
    }

    /// <summary>A finished run of the script, as a worker would have reported it, newer than any before it.</summary>
    private async Task RanAsync(string script, string workspace, bool fresh, long usedBytes)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        var latest = await db.Jobs.Where(j => j.CompletedAt != null).MaxAsync(j => (DateTime?)j.CompletedAt) ?? DateTime.UtcNow.AddHours(-1);
        db.Jobs.Add(new Job
        {
            Id = Guid.NewGuid(),
            Status = JobStatus.Success,
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = script,
            PriorityId = await _fixture.PriorityIdAsync(PriorityKind.Job, StandardPriority.Medium),
            CreatedAt = latest,
            StartedAt = latest,
            CompletedAt = latest.AddSeconds(1),
            WorkspaceKind = WorkspaceKind.Named,
            WorkspaceName = workspace,
            WorkspaceFresh = fresh,
            DiskUsedBytes = usedBytes,
            StoppedForDisk = false
        });
        await db.SaveChangesAsync();
    }

    private void AddScript(string script, string workspace)
    {
        _repo.AddFile(script, $$"""
            Workspace("{{workspace}}");
            Step("check")
                .Image("alpine:latest")
                .Run(async ctx => await ctx.Exec("echo", "Hello"));
            """);
        _repo.Commit($"Add {script}");
    }

    private async Task<JobInfo> RunAsync(string script)
    {
        var jobId = await _fixture.SubmitJobAsync(new JobSubmission { RepoUrl = _repo.Url, Ref = "master", ScriptPath = script });
        return await _fixture.WaitForJobCompletionAsync(jobId);
    }

    private async Task<WorkerView> WaitForWorkerAsync(Guid workerId, Func<WorkerView, bool> done, string what)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            using var scope = _fixture.Services.CreateScope();
            var view = (await scope.ServiceProvider.GetRequiredService<WorkerRepository>().GetByIdAsync(workerId))!;
            if (done(view))
            {
                return view;
            }
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(30))
            {
                Assert.Fail($"never saw {what}");
            }
            await Task.Delay(100);
        }
    }

    private async Task<Job> JobRowAsync(Guid jobId)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>().Jobs.AsNoTracking().FirstAsync(j => j.Id == jobId);
    }

    private static DiskSpaceReport Free(long gb)
    {
        return new DiskSpaceReport { FreeBytes = gb * Gb, TotalBytes = 1000 * Gb, MinFreeBytes = Gb };
    }

    /// <summary>A paused worker with a roomy disk: no job goes to it, but a need is never taken to be more than it could give, so it keeps the needs in these tests from being capped.</summary>
    private async Task RoomyButPausedAsync()
    {
        var roomy = await DirectedWorker.ReadyAsync(_fixture, "roomy-paused", "conn-roomy-paused");
        await Scheduler.SetWorkerPausedAsync(roomy.WorkerId, true);
        await roomy.Hub.ReportDiskSpace(Free(500));
    }

    private static WorkspaceUsageReport Usage(params (string Name, long Gb)[] workspaces)
    {
        return new WorkspaceUsageReport { WorkspaceSizes = workspaces.ToDictionary(w => w.Name, w => (long?)(w.Gb * Gb)), MirrorsSizeBytes = 0 };
    }

    private static readonly WorkspaceUsageReport NoWorkspaces = Usage();

    /// <summary>A ready worker whose disk, 1000 GB, has this much free and the rest in use by something other than Ilmarinen.</summary>
    private async Task<DirectedWorker> FullWorkerAsync(string name, long freeGb)
    {
        var worker = await DirectedWorker.ReadyAsync(_fixture, name, $"conn-{name}");
        await worker.Hub.ReportWorkspaceUsage(NoWorkspaces);
        await worker.Hub.ReportDiskSpace(Free(freeGb));
        return worker;
    }

    private Task<Guid> EnqueueAsync(string script, string? minWorkerPriority = null)
    {
        return Scheduler.EnqueueJobAsync(new JobSubmission { RepoUrl = _repo.Url, Ref = "master", ScriptPath = script, GitTokenMode = GitTokenMode.None, MinWorkerPriority = minWorkerPriority });
    }

    [Test]
    public async Task AJobWithoutRoom_WaitsAndIsReported_UntilTheWorkerReportsRoom()
    {
        await RanAsync("big.csx", "big-ws", fresh: true, usedBytes: 50 * Gb);
        await RoomyButPausedAsync();
        var worker = await DirectedWorker.ReadyAsync(_fixture, "room-worker", "conn-room");
        await worker.Hub.ReportDiskSpace(Free(10));

        var jobId = await Scheduler.EnqueueJobAsync(new JobSubmission { RepoUrl = _repo.Url, Ref = "master", ScriptPath = "big.csx", GitTokenMode = GitTokenMode.None });

        Assert.That((await JobRowAsync(jobId)).Status, Is.EqualTo(JobStatus.Queued));
        var report = await Scheduler.GetUnsatisfiableJobsAsync();
        Assert.That(report.Jobs, Is.Empty, "it isn't waiting for priority");
        var noRoom = report.NoRoomJobs.Single();
        Assert.That(noRoom.Job.Id, Is.EqualTo(jobId));
        Assert.That(noRoom.NeededBytes, Is.EqualTo(50 * Gb));
        Assert.That(noRoom.MostRoomBytes, Is.EqualTo(9 * Gb), "10 free less the 1 kept free");

        // Nothing but the report: room appearing must be enough to dispatch.
        await worker.Hub.ReportDiskSpace(Free(100));

        var job = await JobRowAsync(jobId);
        Assert.That((job.Status, job.WorkerId), Is.EqualTo((JobStatus.Running, (Guid?)worker.WorkerId)));
        var assignment = (await worker.SentAsync()).Assignments.Single();
        Assert.That(assignment.PredictedWorkspace, Is.EqualTo("big-ws"));
        Assert.That(assignment.DiskNeededBytes, Is.EqualTo(50 * Gb));
        Assert.That(assignment.StartFresh, Is.False);
        Assert.That((await Scheduler.GetUnsatisfiableJobsAsync()).NoRoomJobs, Is.Empty);
    }

    [Test]
    public async Task AHolderWithRoomNeitherToReuseNorToStartAfresh_IsReported_WithWhatReusingNeeds()
    {
        await RanAsync("ws.csx", "ws", fresh: true, usedBytes: 20 * Gb);
        await RanAsync("ws.csx", "ws", fresh: false, usedBytes: 8 * Gb);
        await RoomyButPausedAsync();
        var holder = await DirectedWorker.RegisterAsync(_fixture, "holder");
        await holder.ConnectAsync("conn-holder", workspaces: [new WorkspaceInfo { Name = "ws", Path = "/data/workspaces/ws", LastUsed = DateTimeOffset.UtcNow }]);
        await holder.ResumeAsync(holder.Idle());
        await holder.Hub.ReportWorkspaceUsage(new WorkspaceUsageReport { WorkspaceSizes = new System.Collections.Generic.Dictionary<string, long?> { ["ws"] = 10 * Gb }, MirrorsSizeBytes = 0 });
        await holder.Hub.ReportDiskSpace(Free(5));

        // Reusing needs 8 + 1 of the 5 free; afresh, 20 + 1 of the 15 deleting it would leave.
        var jobId = await Scheduler.EnqueueJobAsync(new JobSubmission { RepoUrl = _repo.Url, Ref = "master", ScriptPath = "ws.csx", GitTokenMode = GitTokenMode.None });

        Assert.That((await JobRowAsync(jobId)).Status, Is.EqualTo(JobStatus.Queued));
        var noRoom = (await Scheduler.GetUnsatisfiableJobsAsync()).NoRoomJobs.Single();
        Assert.That(noRoom.Job.Id, Is.EqualTo(jobId));
        Assert.That(noRoom.NeededBytes, Is.EqualTo(8 * Gb));
        Assert.That(noRoom.MostRoomBytes, Is.EqualTo(4 * Gb), "what reusing has to go on, without the copy it reuses");
    }

    [Test]
    public async Task AJobWithoutRoom_IsReported_WithTheNeedAndRoomOfTheWorkerNearestToHavingIt()
    {
        await RanAsync("ws.csx", "ws", fresh: true, usedBytes: 50 * Gb);
        await RanAsync("ws.csx", "ws", fresh: false, usedBytes: 8 * Gb);
        await RoomyButPausedAsync();
        // Reusing its copy, the holder needs 8 and has 5 - 1 = 4: 4 short. Afresh, the other needs 50 and has 46 - 1 = 45: 5 short.
        var holder = await DirectedWorker.RegisterAsync(_fixture, "holder");
        await holder.ConnectAsync("conn-holder", workspaces: [new WorkspaceInfo { Name = "ws", Path = "/data/workspaces/ws", LastUsed = DateTimeOffset.UtcNow }]);
        await holder.ResumeAsync(holder.Idle());
        await holder.Hub.ReportWorkspaceUsage(Usage(("ws", 10)));
        await holder.Hub.ReportDiskSpace(Free(5));
        await FullWorkerAsync("other", freeGb: 46);

        await EnqueueAsync("ws.csx");

        var noRoom = (await Scheduler.GetUnsatisfiableJobsAsync()).NoRoomJobs.Single();
        Assert.That((noRoom.NeededBytes, noRoom.MostRoomBytes), Is.EqualTo((8 * Gb, (long?)(4 * Gb))), "one worker's numbers, not one's need and another's room");
    }

    [Test]
    public async Task AWorkerBelowItsFloor_MakesRoomFirst_AndTheJobRuns()
    {
        var workerId = await _fixture.StartWorkerAsync();
        AddScript("a.csx", "old-a");
        AddScript("next.csx", "next-ws");
        Assert.That((await RunAsync("a.csx")).Status, Is.EqualTo(JobStatus.Success));
        await WaitForWorkerAsync(workerId, v => v.Workspaces.Any(w => w.Name == "old-a" && w.Measured), "the workspace measured");

        // The server last heard of plenty; by the time the job arrives the disk is below the floor, until the old workspace goes.
        var oldA = Path.Combine(_fixture.WorkerWorkspacePath!, "old-a");
        _fixture.WorkerDisk.Follow = () => Directory.Exists(oldA) ? Gb / 2 : 20 * Gb;
        var job = await RunAsync("next.csx");

        Assert.That(job.Status, Is.EqualTo(JobStatus.Success), "the watch starts only once room is made");
        Assert.That(job.DiskUse!.StoppedForDisk, Is.False);
        Assert.That(Directory.Exists(oldA), Is.False);
    }

    [Test]
    public async Task MakingRoom_DeletesTheLeastRecentlyUsedWorkspace_AndNoMore()
    {
        var workerId = await _fixture.StartWorkerAsync();
        AddScript("a.csx", "old-a");
        AddScript("b.csx", "old-b");
        AddScript("big.csx", "big-ws");
        Assert.That((await RunAsync("a.csx")).Status, Is.EqualTo(JobStatus.Success));
        Assert.That((await RunAsync("b.csx")).Status, Is.EqualTo(JobStatus.Success));
        await WaitForWorkerAsync(workerId, v => v.Workspaces.Count(w => w.Measured && w.SizeBytes > 0) == 2, "both workspaces measured");

        // Short by a byte of what the next run took last time, plus the 1 GB floor: deleting the older workspace covers it.
        await RanAsync("big.csx", "big-ws", fresh: true, usedBytes: 10 * Gb);
        _fixture.WorkerDisk.FreeBytes = 11 * Gb - 1;
        await WaitForWorkerAsync(workerId, v => v.DiskSpace?.FreeBytes == 11 * Gb - 1, "the free space reported");
        var job = await RunAsync("big.csx");

        Assert.That(job.Status, Is.EqualTo(JobStatus.Success));
        var root = _fixture.WorkerWorkspacePath!;
        Assert.That(Directory.Exists(Path.Combine(root, "old-a")), Is.False, "least recently used");
        Assert.That(Directory.Exists(Path.Combine(root, "old-b")), Is.True, "deleting the first was enough");
        Assert.That(Directory.Exists(Path.Combine(root, "big-ws")), Is.True);
        await WaitForWorkerAsync(workerId, v => !v.Workspaces.Any(w => w.Name == "old-a"), "the server forgetting the deleted workspace");
    }

    [Test]
    public async Task AWorkspaceThatGrewTooBigToReuse_IsMadeAfresh()
    {
        var workerId = await _fixture.StartWorkerAsync();
        AddScript("hog.csx", "hog");
        Assert.That((await RunAsync("hog.csx")).Status, Is.EqualTo(JobStatus.Success));
        await WaitForWorkerAsync(workerId, v => v.Workspaces.Any(w => w.Name == "hog" && w.Measured), "the workspace measured");

        // Reusing it took 10 GB last time; a fresh start takes next to nothing, and there are 5 GB to spare.
        await RanAsync("hog.csx", "hog", fresh: true, usedBytes: 1024 * 1024);
        await RanAsync("hog.csx", "hog", fresh: false, usedBytes: 10 * Gb);
        _fixture.WorkerDisk.FreeBytes = 6 * Gb;
        await WaitForWorkerAsync(workerId, v => v.DiskSpace?.FreeBytes == 6 * Gb, "the free space reported");
        var job = await RunAsync("hog.csx");

        Assert.That(job.Status, Is.EqualTo(JobStatus.Success));
        Assert.That(job.DiskUse!.WorkspaceName, Is.EqualTo("hog"));
        Assert.That(job.DiskUse.WorkspaceFresh, Is.True, "the worker deleted its copy first");
    }
}
