using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Net;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
[Category("Integration")]
public class WorkerConnectionTests
{
    private IntegrationTestFixture _fixture = null!;
    private TestGitRepository _repo = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new IntegrationTestFixture();
        await _fixture.SetupAsync();

        _repo = new TestGitRepository();
        _repo.AddFile("pipeline.csx", """
            Step("quick")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Exec("echo", "done");
                });
            """);
        _repo.Commit("Add pipeline");
    }

    [TearDown]
    public async Task TearDown()
    {
        _repo.Dispose();
        await _fixture.DisposeAsync();
    }

    [Test]
    public async Task Worker_WhenStarted_RegistersWithServer()
    {
        // Act
        var workerId = await _fixture.StartWorkerAsync();

        // Assert - Worker registration succeeded (would have thrown if not)
        Assert.That(workerId, Is.Not.EqualTo(Guid.Empty));
    }

    [Test]
    public async Task Worker_WhenConnected_RecordsLastIpAddress()
    {
        var workerId = await _fixture.StartWorkerAsync(diagnostic: null, waitForReady: false);

        using var scope = _fixture.Services.CreateScope();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();
        var view = await workers.GetByIdAsync(workerId);

        Assert.That(view, Is.Not.Null);
        Assert.That(view!.LastIpAddress, Is.Not.Null, "worker's connecting IP should have been recorded");

        // The fixture binds localhost on both stacks, so the peer is 127.0.0.1 or ::1 depending on which the client picked — assert the property, not the literal.
        var address = IPAddress.Parse(view.LastIpAddress!);
        Assert.That(IPAddress.IsLoopback(address), Is.True, $"expected a loopback address, got {view.LastIpAddress}");
    }

    [Test]
    public async Task Worker_WhenDisconnected_RetainsLastIpAddress()
    {
        var workerId = await _fixture.StartWorkerAsync(diagnostic: null, waitForReady: false);

        string? connectedIp;
        using (var scope = _fixture.Services.CreateScope())
        {
            var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();
            connectedIp = (await workers.GetByIdAsync(workerId))!.LastIpAddress;
        }
        Assert.That(connectedIp, Is.Not.Null);

        await _fixture.StopWorkerAsync();
        await _fixture.WaitForWorkerDisconnectAsync(workerId);

        using var afterScope = _fixture.Services.CreateScope();
        var afterWorkers = afterScope.ServiceProvider.GetRequiredService<WorkerRepository>();
        var view = await afterWorkers.GetByIdAsync(workerId);

        Assert.That(view!.IsConnected, Is.False);
        Assert.That(view.LastIpAddress, Is.EqualTo(connectedIp), "last IP must survive the worker going away — that's exactly when an operator needs it");
    }

    [Test]
    public async Task Worker_WhenConnectionHasNoAddress_KeepsPreviousLastIpAddress()
    {
        using var scope = _fixture.Services.CreateScope();
        var registration = scope.ServiceProvider.GetRequiredService<WorkerRegistrationService>();
        var result = await registration.RegisterWorkerAsync($"addressless-{Guid.NewGuid():N}");

        var workers = _fixture.Services.GetRequiredService<WorkerRepository>();
        await workers.ConnectAsync("conn-with-address", result.WorkerId, "10.0.0.5");
        await workers.ConnectAsync("conn-without-address", result.WorkerId, ipAddress: null);

        var view = await workers.GetByIdAsync(result.WorkerId);
        Assert.That(view!.LastIpAddress, Is.EqualTo("10.0.0.5"), "an address-less connection must not blank out a known address");
    }

    [Test]
    public async Task Job_WhenWorkerNotAvailable_StaysQueued()
    {
        // Arrange - No worker started
        var submission = new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        };

        // Act
        var jobId = await _fixture.SubmitJobAsync(submission);
        await Task.Delay(1000); // Give it time to potentially (incorrectly) run

        // Assert
        var job = await _fixture.GetJobAsync(jobId);
        Assert.That(job.Status, Is.EqualTo(JobStatus.Queued));
    }

    [Test]
    public async Task Job_WhenWorkerStartsLater_GetsExecuted()
    {
        // Arrange - Submit job first (no worker yet)
        var submission = new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        };
        var jobId = await _fixture.SubmitJobAsync(submission);

        // Verify queued
        var queuedJob = await _fixture.GetJobAsync(jobId);
        Assert.That(queuedJob.Status, Is.EqualTo(JobStatus.Queued));

        // Act - Start worker
        await _fixture.StartWorkerAsync();

        // Assert - Job should complete
        var completedJob = await _fixture.WaitForJobCompletionAsync(jobId);
        Assert.That(completedJob.Status, Is.EqualTo(JobStatus.Success));
    }
}
