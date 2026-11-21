using Ilmarinen.Database;
using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>What a run worked in and the disk it took, as its worker reports and the server stores it; and a run stopped before it fills the disk.</summary>
[TestFixture]
[Category("Integration")]
public class DiskUseTests
{
    private const long Gb = FakeDiskSpace.Gb;

    private IntegrationTestFixture _fixture = null!;
    private TestGitRepository _repo = null!;
    private Guid _workerId;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new IntegrationTestFixture();
        await _fixture.SetupAsync();
        _workerId = await _fixture.StartWorkerAsync();
        _repo = new TestGitRepository();
    }

    [TearDown]
    public async Task TearDown()
    {
        _repo.Dispose();
        await _fixture.DisposeAsync();
    }

    private void AddPipeline(string script)
    {
        _repo.AddFile("pipeline.csx", script);
        _repo.Commit("Add pipeline");
    }

    private async Task<JobInfo> RunAsync(string scriptPath = "pipeline.csx")
    {
        var jobId = await _fixture.SubmitJobAsync(new JobSubmission { RepoUrl = _repo.Url, Ref = "master", ScriptPath = scriptPath });
        return await _fixture.WaitForJobCompletionAsync(jobId);
    }

    [Test]
    public async Task NamedWorkspace_IsFreshTheFirstTime_ReusedAfter()
    {
        AddPipeline("""
            Workspace("disk-ws");
            Step("check")
                .Image("alpine:latest")
                .Run(async ctx => await ctx.Exec("echo", "Hello"));
            """);

        var first = await RunAsync();
        var second = await RunAsync();

        Assert.That(first.DiskUse, Is.Not.Null);
        Assert.That(first.DiskUse!.WorkspaceKind, Is.EqualTo(WorkspaceKind.Named));
        Assert.That(first.DiskUse.WorkspaceName, Is.EqualTo("disk-ws"));
        Assert.That(first.DiskUse.WorkspaceFresh, Is.True);
        Assert.That(first.DiskUse.UsedBytes, Is.Not.Null);
        Assert.That(first.DiskUse.StoppedForDisk, Is.False);
        Assert.That(second.DiskUse!.WorkspaceFresh, Is.False);
    }

    [Test]
    public async Task NoWorkspace_IsEphemeral()
    {
        AddPipeline("""
            Step("check")
                .Image("alpine:latest")
                .Run(async ctx => await ctx.Exec("echo", "Hello"));
            """);

        var job = await RunAsync();

        Assert.That(job.DiskUse!.WorkspaceKind, Is.EqualTo(WorkspaceKind.Ephemeral));
        Assert.That(job.DiskUse.WorkspaceName, Is.Null);
    }

    [Test]
    public async Task ScriptMissing_WorkspaceIsUnknown()
    {
        AddPipeline("""Step("check").Image("alpine:latest").Run(async ctx => await ctx.Exec("true"));""");

        var job = await RunAsync("missing.csx");

        Assert.That(job.Status, Is.EqualTo(JobStatus.Failed));
        Assert.That(job.DiskUse!.WorkspaceKind, Is.EqualTo(WorkspaceKind.Unknown));
    }

    [Test]
    public async Task Used_IsHowFarFreeSpaceFell()
    {
        AddPipeline("""
            Step("wait")
                .Image("alpine:latest")
                .Run(async ctx => await ctx.Exec("sleep", "8"));
            """);
        var free = _fixture.WorkerDisk.FreeBytes;

        var jobId = await _fixture.SubmitJobAsync(new JobSubmission { RepoUrl = _repo.Url, Ref = "master", ScriptPath = "pipeline.csx" });
        await _fixture.WaitForJobStatusAsync(jobId, JobStatus.Running);
        await Task.Delay(TimeSpan.FromSeconds(2));
        _fixture.WorkerDisk.FreeBytes = free - 100 * Gb;
        await Task.Delay(TimeSpan.FromSeconds(3));
        _fixture.WorkerDisk.FreeBytes = free;
        var job = await _fixture.WaitForJobCompletionAsync(jobId);

        Assert.That(job.Status, Is.EqualTo(JobStatus.Success));
        Assert.That(job.DiskUse!.UsedBytes, Is.EqualTo(100 * Gb));
    }

    [Test]
    public async Task FreeSpaceBelowTheFloor_StopsTheJob_WithoutOwingAHostCheck()
    {
        AddPipeline("""
            Step("wait")
                .Image("alpine:latest")
                .Run(async ctx => await ctx.Exec("sleep", "120"));
            """);

        var jobId = await _fixture.SubmitJobAsync(new JobSubmission { RepoUrl = _repo.Url, Ref = "master", ScriptPath = "pipeline.csx" });
        await _fixture.WaitForJobStatusAsync(jobId, JobStatus.Running);
        await Task.Delay(TimeSpan.FromSeconds(2));
        _fixture.WorkerDisk.FreeBytes = Gb / 2;
        var job = await _fixture.WaitForJobCompletionAsync(jobId, timeoutMs: 60_000);
        _fixture.WorkerDisk.FreeBytes = 500 * Gb;

        Assert.That(job.Status, Is.EqualTo(JobStatus.Failed));
        Assert.That(job.DiskUse!.StoppedForDisk, Is.True);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        var owed = await db.Workers.Where(w => w.Id == _workerId).Select(w => w.DiagnosticOwed).SingleAsync();
        Assert.That(owed, Is.False, "running out of disk says nothing about the host's health");
    }
}
