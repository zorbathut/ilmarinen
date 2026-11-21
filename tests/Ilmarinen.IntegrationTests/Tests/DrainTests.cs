using Ilmarinen.Database;
using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.NotificationClient;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Linq;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>Draining the fleet before a deploy, through the API the CLI uses: no new job starts, what is running finishes, and releasing the drain dispatches.</summary>
[TestFixture]
[Category("Integration")]
public class DrainTests
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

    private JobScheduler Scheduler
    {
        get { return _fixture.Services.GetRequiredService<JobScheduler>(); }
    }

    private static JobSubmission Submission()
    {
        return new JobSubmission
        {
            RepoUrl = "https://example.invalid/repo.git",
            Ref = "master",
            ScriptPath = "pipeline.csx",
            GitTokenMode = GitTokenMode.None
        };
    }

    private async Task<DirectedWorker> ReadyWorkerAsync(string name)
    {
        var worker = await DirectedWorker.RegisterAsync(_fixture, name);
        await worker.ConnectAsync($"conn-{name}");
        await worker.ResumeAsync(worker.Idle());
        return worker;
    }

    private async Task<DrainStatus> DrainAsync(bool draining)
    {
        var response = await _fixture.HttpClient.PutAsJsonAsync("/api/drain", new DrainUpdate { Draining = draining });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DrainStatus>())!;
    }

    private async Task<DrainStatus> StatusAsync()
    {
        return (await _fixture.HttpClient.GetFromJsonAsync<DrainStatus>("/api/drain"))!;
    }

    private async Task<JobStatus> JobStatusAsync(Guid jobId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        return (await db.Jobs.FirstAsync(j => j.Id == jobId)).Status;
    }

    [Test]
    public async Task WhileDraining_AQueuedJobWaits_AndReleasingDispatchesIt()
    {
        var worker = await ReadyWorkerAsync("drain-ready");
        Assert.That((await DrainAsync(true)).Draining, Is.True);

        var jobId = await Scheduler.EnqueueJobAsync(Submission());

        Assert.That(await JobStatusAsync(jobId), Is.EqualTo(JobStatus.Queued));
        Assert.That((await worker.SentAsync()).Assignments, Is.Empty);
        Assert.That(await worker.IsReadyAsync(), Is.True, "a drain holds dispatch, not the workers: they stay ready");
        using (var client = new IlmarinenNotificationClient(_fixture.HttpClient.BaseAddress!.ToString(), _fixture.HttpClient))
        {
            var stuck = await client.GetUnsatisfiableJobsAsync(CancellationToken.None);
            Assert.That(stuck.Jobs, Is.Empty, "a held queue isn't a stuck one: the bot mustn't alert on a drain");
            Assert.That(stuck.NoRoomJobs, Is.Empty);
        }

        Assert.That((await DrainAsync(false)).Draining, Is.False);

        Assert.That(await JobStatusAsync(jobId), Is.EqualTo(JobStatus.Running), "releasing the drain dispatches; nothing else would");
        Assert.That((await worker.SentAsync()).Assignments.Select(a => a.Id), Is.EqualTo(new[] { jobId }));
    }

    [Test]
    public async Task ARunningJob_FinishesDuringADrain_AndTheStatusEmpties()
    {
        var worker = await ReadyWorkerAsync("drain-running");
        var jobId = await Scheduler.EnqueueJobAsync(Submission());
        var assignment = (await worker.SentAsync()).Assignments.Single();
        await worker.ReportAsync(worker.Running(jobId, assignment.AssignmentId));

        var draining = await DrainAsync(true);
        Assert.That(draining.Running, Is.EqualTo(new[] { new DrainRunningJob(jobId, "drain-running") }));

        await worker.ReportAsync(DirectedWorker.Completed(jobId, assignment.AssignmentId, JobStatus.Success));
        await worker.ReportAsync(worker.Idle());

        var after = await StatusAsync();
        Assert.That(await JobStatusAsync(jobId), Is.EqualTo(JobStatus.Success));
        Assert.That(after.Draining, Is.True);
        Assert.That(after.Running, Is.Empty, "nothing left to wait for: ready to deploy");
    }

    [Test]
    public async Task TheStatus_WithoutADrain_SaysSo()
    {
        var status = await StatusAsync();

        Assert.That(status.Draining, Is.False);
        Assert.That(status.Running, Is.Empty);
    }
}
