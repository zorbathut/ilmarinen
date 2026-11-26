using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// Deleting a workspace the running job is using, driven through the hub: the worker refuses, the server queues the delete, and sends it again once the worker says the job has let the workspace go, before it can hand the worker its next job.
/// </summary>
[TestFixture]
[Category("Integration")]
public class QueuedWorkspaceDeleteTests
{
    private const string Workspace = "busy-ws";
    private static readonly DeleteWorkspaceResult InUse = new() { Status = DeleteWorkspaceStatus.InUse, Error = "Workspace is currently in use by a running job." };
    private static readonly DeleteWorkspaceResult Gone = new() { Status = DeleteWorkspaceStatus.Success };

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
    public async Task ADeleteOfAWorkspaceInUse_WaitsForTheJob_AndGoesOutBeforeTheNextAssignment()
    {
        var worker = await ConnectedAsync("queued-delete", "conn-queued-delete");
        var (jobId, assignment) = await StartOnWorkspaceAsync(worker);

        var result = await RequestRefusedAsync(worker);

        Assert.That(result.Status, Is.EqualTo(DeleteWorkspaceStatus.InUse));
        Assert.That(await IsQueuedAsync(worker), Is.True);
        Assert.That(await DeletesSentAsync(worker), Is.EqualTo(1), "nothing more goes out while the job holds it");

        // A job waiting for the worker, which the idle report frees it to take.
        var next = await Scheduler.EnqueueJobAsync(Submission());
        await worker.ReportAsync(DirectedWorker.Completed(jobId, assignment, JobStatus.Success));
        Assert.That(await DeletesSentAsync(worker), Is.EqualTo(1), "the outcome doesn't say the job has let the workspace go");
        await worker.ReportAsync(worker.Idle());

        var messages = (await worker.SentAsync()).Messages;
        var delete = messages.FindLastIndex(m => m == $"DeleteWorkspace {Workspace}");
        var assigned = messages.FindIndex(m => m == $"AssignJob {next}");
        Assert.That(await DeletesSentAsync(worker), Is.EqualTo(2));
        Assert.That(assigned, Is.GreaterThan(delete), string.Join("\n", messages));
        Assert.That(await IsQueuedAsync(worker), Is.True, "until the worker answers");
        await worker.Hub.WorkspaceDeleted(Workspace, Gone);
        Assert.That(IsQueued(worker), Is.False);
    }

    [Test]
    public async Task ARefusalThatArrivesAfterTheJobLetTheWorkspaceGo_SendsTheDeleteAtOnce()
    {
        var worker = await ConnectedAsync("late-refusal", "conn-late-refusal");
        var (jobId, assignment) = await StartOnWorkspaceAsync(worker);

        var request = DeletionService.RequestAsync(worker.WorkerId, Workspace);
        await WaitForDeletesAsync(worker, 1);
        await worker.ReportAsync(DirectedWorker.Completed(jobId, assignment, JobStatus.Success));
        await worker.ReportAsync(worker.Idle());
        await worker.Hub.WorkspaceDeleted(Workspace, InUse);
        await request;

        Assert.That(await DeletesSentAsync(worker), Is.EqualTo(2));
        Assert.That(await IsQueuedAsync(worker), Is.True, "until the worker answers");
        await worker.Hub.WorkspaceDeleted(Workspace, Gone);
        Assert.That(IsQueued(worker), Is.False);
    }

    [Test]
    public async Task AQueuedDeleteRefusedAgain_IsQueuedAgain()
    {
        var worker = await ConnectedAsync("refused-again", "conn-refused-again");
        var (jobId, assignment) = await StartOnWorkspaceAsync(worker);
        await RequestRefusedAsync(worker);
        var next = await Scheduler.EnqueueJobAsync(Submission());
        await worker.ReportAsync(DirectedWorker.Completed(jobId, assignment, JobStatus.Success));
        await worker.ReportAsync(worker.Idle());
        Assert.That(await DeletesSentAsync(worker), Is.EqualTo(2));

        // The next job took the workspace before the delete reached the worker.
        var nextAssignment = (await worker.SentAsync()).Assignments.Last(a => a.Id == next).AssignmentId;
        await worker.ReportAsync(worker.Running(next, nextAssignment) with { Workspace = Workspace });
        await worker.Hub.WorkspaceDeleted(Workspace, InUse);
        Assert.That(await IsQueuedAsync(worker), Is.True);
        Assert.That(await DeletesSentAsync(worker), Is.EqualTo(2));

        await worker.ReportAsync(DirectedWorker.Completed(next, nextAssignment, JobStatus.Success));
        await worker.ReportAsync(worker.Idle());
        Assert.That(await DeletesSentAsync(worker), Is.EqualTo(3));
        Assert.That(await IsQueuedAsync(worker), Is.True, "until the worker answers");
        await worker.Hub.WorkspaceDeleted(Workspace, Gone);
        Assert.That(IsQueued(worker), Is.False);
    }

    [Test]
    public async Task AQueuedDeleteLostWithItsConnection_GoesOutAgainOnTheNextOne()
    {
        var worker = await ConnectedAsync("lost-delete", "conn-lost-delete");
        var (jobId, assignment) = await StartOnWorkspaceAsync(worker);
        await RequestRefusedAsync(worker);
        await worker.ReportAsync(DirectedWorker.Completed(jobId, assignment, JobStatus.Success));
        await worker.ReportAsync(worker.Idle());
        Assert.That(await DeletesSentAsync(worker), Is.EqualTo(2));

        // The connection goes before the worker acts on the delete: idle, its host went to sleep, say.
        await worker.Hub.OnDisconnectedAsync(null);
        await worker.ConnectAsync("conn-lost-delete-2", workspaces: [new WorkspaceInfo { Name = Workspace, Path = $"/ws/{Workspace}", LastUsed = DateTimeOffset.UnixEpoch }]);
        await worker.ResumeAsync(worker.Idle());

        Assert.That(await DeletesSentAsync(worker), Is.EqualTo(1), "sent again on the new connection");
        await worker.Hub.WorkspaceDeleted(Workspace, Gone);
        Assert.That(IsQueued(worker), Is.False);
    }

    [Test]
    public async Task ARefusalOfADeleteNobodyQueued_QueuesNothing()
    {
        var worker = await ConnectedAsync("unasked-refusal", "conn-unasked-refusal");
        await StartOnWorkspaceAsync(worker);

        // A request that timed out before the worker answered, or the worker's own room-making.
        await worker.Hub.WorkspaceDeleted(Workspace, InUse);

        Assert.That(IsQueued(worker), Is.False);
    }

    [Test]
    public async Task AQueuedDeleteThatFails_IsDropped()
    {
        var worker = await ConnectedAsync("failed-delete", "conn-failed-delete");
        var (jobId, assignment) = await StartOnWorkspaceAsync(worker);
        await RequestRefusedAsync(worker);
        await worker.ReportAsync(DirectedWorker.Completed(jobId, assignment, JobStatus.Success));
        await worker.ReportAsync(worker.Idle());

        await worker.Hub.WorkspaceDeleted(Workspace, new DeleteWorkspaceResult { Status = DeleteWorkspaceStatus.Error, Error = "disk on fire" });

        Assert.That(IsQueued(worker), Is.False);
        await worker.ReportAsync(worker.Idle());
        Assert.That(await DeletesSentAsync(worker), Is.EqualTo(2), "not tried again");
    }

    private JobScheduler Scheduler
    {
        get
        {
            return _fixture.Services.GetRequiredService<JobScheduler>();
        }
    }

    private WorkspaceDeletionService DeletionService
    {
        get
        {
            return _fixture.Services.GetRequiredService<WorkspaceDeletionService>();
        }
    }

    private static JobSubmission Submission()
    {
        return new JobSubmission { RepoUrl = "/nonexistent/repo", Ref = "master", ScriptPath = "pipeline.csx" };
    }

    private async Task<DirectedWorker> ConnectedAsync(string name, string connectionId)
    {
        var worker = await DirectedWorker.RegisterAsync(_fixture, name);
        await worker.ConnectAsync(connectionId, workspaces: [new WorkspaceInfo { Name = Workspace, Path = $"/ws/{Workspace}", LastUsed = DateTimeOffset.UnixEpoch }]);
        await worker.ResumeAsync(worker.Idle());
        return worker;
    }

    private async Task<(Guid JobId, Guid AssignmentId)> StartOnWorkspaceAsync(DirectedWorker worker)
    {
        var jobId = await Scheduler.EnqueueJobAsync(Submission());
        var assignment = (await worker.SentAsync()).Assignments.Last(a => a.Id == jobId).AssignmentId;
        await worker.ReportAsync(worker.Running(jobId, assignment) with { Workspace = Workspace });
        return (jobId, assignment);
    }

    /// <summary>Asks for the delete, and answers as the worker does while its job holds the workspace.</summary>
    private async Task<DeleteWorkspaceResult> RequestRefusedAsync(DirectedWorker worker)
    {
        var request = DeletionService.RequestAsync(worker.WorkerId, Workspace);
        await WaitForDeletesAsync(worker, 1);
        await worker.Hub.WorkspaceDeleted(Workspace, InUse);
        return await request;
    }

    private async Task<int> DeletesSentAsync(DirectedWorker worker)
    {
        return (await worker.SentAsync()).Messages.Count(m => m == $"DeleteWorkspace {Workspace}");
    }

    private async Task WaitForDeletesAsync(DirectedWorker worker, int count)
    {
        var deadline = Stopwatch.StartNew();
        while (await DeletesSentAsync(worker) < count)
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(10))
            {
                Assert.Fail($"The server never sent DeleteWorkspace {Workspace}");
            }
            await Task.Delay(20);
        }
    }

    private bool IsQueued(DirectedWorker worker)
    {
        return _fixture.Services.GetRequiredService<WorkerRepository>().IsDeleteQueued(worker.WorkerId, Workspace);
    }

    /// <summary>As the worker's view shows it.</summary>
    private async Task<bool> IsQueuedAsync(DirectedWorker worker)
    {
        var view = await _fixture.Services.GetRequiredService<WorkerRepository>().GetByIdAsync(worker.WorkerId);
        return view!.Workspaces.Single(w => w.Name == Workspace).DeletePending;
    }
}
