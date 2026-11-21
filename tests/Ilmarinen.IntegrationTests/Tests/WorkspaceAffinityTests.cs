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
using System.Collections.Generic;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>A job goes to a worker already holding the persistent workspace its script's latest run used, among workers of the same priority.</summary>
[TestFixture]
[Category("Integration")]
public class WorkspaceAffinityTests
{
    private const string RepoUrl = "https://example.invalid/affinity.git";
    private const string Script = "pipeline.csx";

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

    /// <summary>A finished run of the script, as a worker would have reported it.</summary>
    private async Task RanAsync(WorkspaceKind kind, string? workspace, DateTime completedAt)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        db.Jobs.Add(new Job
        {
            Id = Guid.NewGuid(),
            Status = JobStatus.Success,
            RepoUrl = RepoUrl,
            Ref = "master",
            ScriptPath = Script,
            PriorityId = await _fixture.PriorityIdAsync(PriorityKind.Job, StandardPriority.Medium),
            CreatedAt = completedAt.AddMinutes(-5),
            StartedAt = completedAt.AddMinutes(-4),
            CompletedAt = completedAt,
            WorkspaceKind = kind,
            WorkspaceName = workspace,
            WorkspaceFresh = false,
            DiskUsedBytes = 0,
            StoppedForDisk = false
        });
        await db.SaveChangesAsync();
    }

    private async Task<DirectedWorker> ReadyWorkerAsync(string connectionId, string priority, params string[] workspaces)
    {
        var worker = await DirectedWorker.RegisterAsync(_fixture, $"wa-{connectionId}");
        await _fixture.Services.GetRequiredService<WorkerRepository>().SetPriorityAsync(worker.WorkerId, await _fixture.PriorityIdAsync(PriorityKind.Worker, priority));
        var held = new List<WorkspaceInfo>();
        foreach (var name in workspaces)
        {
            held.Add(new WorkspaceInfo { Name = name, Path = $"/data/workspaces/{name}", LastUsed = DateTimeOffset.UtcNow });
        }
        await worker.ConnectAsync(connectionId, workspaces: held);
        await worker.ResumeAsync(worker.Idle());
        return worker;
    }

    private async Task<Guid?> WorkerOfAsync(Guid jobId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        return (await db.Jobs.FirstAsync(j => j.Id == jobId)).WorkerId;
    }

    private Task<Guid> SubmitAsync()
    {
        return Scheduler.EnqueueJobAsync(new JobSubmission { RepoUrl = RepoUrl, Ref = "master", ScriptPath = Script, GitTokenMode = GitTokenMode.None });
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task EqualPriority_TheHolderTakesIt(bool holderFirst)
    {
        await RanAsync(WorkspaceKind.Named, "affinity-ws", DateTime.UtcNow.AddHours(-1));
        DirectedWorker holder;
        if (holderFirst)
        {
            holder = await ReadyWorkerAsync("conn-wa-holder", StandardPriority.Medium, "affinity-ws");
            await ReadyWorkerAsync("conn-wa-other", StandardPriority.Medium, "something-else");
        }
        else
        {
            await ReadyWorkerAsync("conn-wa-other", StandardPriority.Medium, "something-else");
            holder = await ReadyWorkerAsync("conn-wa-holder", StandardPriority.Medium, "affinity-ws");
        }

        var jobId = await SubmitAsync();

        Assert.That(await WorkerOfAsync(jobId), Is.EqualTo(holder.WorkerId));
    }

    [Test]
    public async Task AHigherPriorityWorker_StillWins()
    {
        await RanAsync(WorkspaceKind.Named, "affinity-ws", DateTime.UtcNow.AddHours(-1));
        await ReadyWorkerAsync("conn-wa-holder", StandardPriority.Low, "affinity-ws");
        var higher = await ReadyWorkerAsync("conn-wa-higher", StandardPriority.High);

        var jobId = await SubmitAsync();

        Assert.That(await WorkerOfAsync(jobId), Is.EqualTo(higher.WorkerId));
    }

    [Test]
    public async Task OnlyTheLatestRunsWorkspaceCounts()
    {
        await RanAsync(WorkspaceKind.Named, "old-ws", DateTime.UtcNow.AddHours(-2));
        await RanAsync(WorkspaceKind.Named, "new-ws", DateTime.UtcNow.AddHours(-1));
        // A run that broke before its script said what it works in changes nothing.
        await RanAsync(WorkspaceKind.Unknown, null, DateTime.UtcNow.AddMinutes(-30));
        await ReadyWorkerAsync("conn-wa-old", StandardPriority.Medium, "old-ws");
        var current = await ReadyWorkerAsync("conn-wa-new", StandardPriority.Medium, "new-ws");

        var jobId = await SubmitAsync();

        Assert.That(await WorkerOfAsync(jobId), Is.EqualTo(current.WorkerId));
    }
}
