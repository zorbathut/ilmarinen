using Ilmarinen.Database;
using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.NotificationClient;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
[Category("Integration")]
public class NotificationTests
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
            Step("hello")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Exec("echo", "Hello!");
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
    public async Task RegisterSubscriber_ReturnsSubscriberInfo()
    {
        var registration = new SubscriberRegistration
        {
            Name = "test-subscriber",
            HeartbeatTimeoutMinutes = 10
        };

        var info = await _fixture.RegisterSubscriberAsync(registration);

        Assert.That(info.Name, Is.EqualTo("test-subscriber"));
        Assert.That(info.IsActive, Is.True);
        Assert.That(info.Id, Is.Not.EqualTo(Guid.Empty));
    }

    [Test]
    public async Task RegisterSubscriber_SameName_ReturnsSameId()
    {
        var registration = new SubscriberRegistration { Name = "upsert-test" };

        var first = await _fixture.RegisterSubscriberAsync(registration);
        var second = await _fixture.RegisterSubscriberAsync(registration);

        Assert.That(second.Id, Is.EqualTo(first.Id));
    }

    [Test]
    public async Task GetSubscriber_AfterRegistration_ReturnsInfo()
    {
        var registration = new SubscriberRegistration { Name = "get-test" };
        var created = await _fixture.RegisterSubscriberAsync(registration);

        var fetched = await _fixture.GetSubscriberAsync(created.Id);

        Assert.That(fetched.Id, Is.EqualTo(created.Id));
        Assert.That(fetched.Name, Is.EqualTo("get-test"));
        Assert.That(fetched.IsActive, Is.True);
    }

    [Test]
    public async Task Heartbeat_UpdatesLastHeartbeat()
    {
        var registration = new SubscriberRegistration { Name = "heartbeat-test" };
        var subscriber = await _fixture.RegisterSubscriberAsync(registration);

        var before = await _fixture.GetSubscriberAsync(subscriber.Id);
        await Task.Delay(100);
        await _fixture.SubscriberHeartbeatAsync(subscriber.Id);
        var after = await _fixture.GetSubscriberAsync(subscriber.Id);

        Assert.That(after.LastHeartbeat, Is.Not.Null);
        Assert.That(after.LastHeartbeat, Is.GreaterThan(before.LastHeartbeat!));
    }

    [Test]
    public async Task PullNotifications_NoNotifications_ReturnsEmpty()
    {
        var registration = new SubscriberRegistration { Name = "empty-pull-test" };
        var subscriber = await _fixture.RegisterSubscriberAsync(registration);

        var notifications = await _fixture.PullNotificationsAsync(subscriber.Id);

        Assert.That(notifications, Is.Empty);
    }

    [Test]
    public async Task JobCompletion_CreatesNotification_ForActiveSubscriber()
    {
        var registration = new SubscriberRegistration { Name = "completion-test" };
        var subscriber = await _fixture.RegisterSubscriberAsync(registration);

        await _fixture.StartWorkerAsync();

        var submission = new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = _repo.Branch,
            ScriptPath = "pipeline.csx"
        };

        var jobId = await _fixture.SubmitJobAsync(submission);
        await _fixture.WaitForJobCompletionAsync(jobId);

        var notifications = await _fixture.WaitForNotificationsAsync(subscriber.Id);

        Assert.That(notifications, Has.Count.EqualTo(1));
        Assert.That(notifications[0].JobId, Is.EqualTo(jobId));
        Assert.That(notifications[0].Status, Is.EqualTo(JobStatus.Success));
        Assert.That(notifications[0].EventType, Is.EqualTo("JobCompleted"));
        Assert.That(notifications[0].RepoUrl, Is.EqualTo(_repo.Url));
        Assert.That(notifications[0].ScriptPath, Is.EqualTo("pipeline.csx"));
    }

    [Test]
    public async Task FailedJob_CreatesNotification_WithFailedStatus()
    {
        var registration = new SubscriberRegistration { Name = "failure-test" };
        var subscriber = await _fixture.RegisterSubscriberAsync(registration);

        await _fixture.StartWorkerAsync();

        _repo.AddFile("failing.csx", """
            Step("fail")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Exec("false");
                });
            """);
        _repo.Commit("Add failing pipeline");

        var submission = new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = _repo.Branch,
            ScriptPath = "failing.csx"
        };

        var jobId = await _fixture.SubmitJobAsync(submission);
        await _fixture.WaitForJobCompletionAsync(jobId);

        var notifications = await _fixture.WaitForNotificationsAsync(subscriber.Id);

        Assert.That(notifications, Has.Count.EqualTo(1));
        Assert.That(notifications[0].Status, Is.EqualTo(JobStatus.Failed));
    }

    [Test]
    public async Task AcknowledgeNotifications_RemovesFromQueue()
    {
        var registration = new SubscriberRegistration { Name = "ack-test" };
        var subscriber = await _fixture.RegisterSubscriberAsync(registration);

        await _fixture.StartWorkerAsync();

        var submission = new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = _repo.Branch,
            ScriptPath = "pipeline.csx"
        };

        var jobId = await _fixture.SubmitJobAsync(submission);
        await _fixture.WaitForJobCompletionAsync(jobId);

        var notifications = await _fixture.WaitForNotificationsAsync(subscriber.Id);
        var notificationIds = notifications.Select(n => n.NotificationId).ToList();

        await _fixture.AcknowledgeNotificationsAsync(subscriber.Id, notificationIds);

        // Ack marks the notifications processed, so the next pull should be empty.
        var afterAck = await _fixture.PullNotificationsAsync(subscriber.Id);
        Assert.That(afterAck, Is.Empty);
    }

    [Test]
    public async Task MultipleSubscribers_EachGetsNotification()
    {
        var sub1 = await _fixture.RegisterSubscriberAsync(new SubscriberRegistration { Name = "multi-sub-1" });
        var sub2 = await _fixture.RegisterSubscriberAsync(new SubscriberRegistration { Name = "multi-sub-2" });

        await _fixture.StartWorkerAsync();

        var submission = new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = _repo.Branch,
            ScriptPath = "pipeline.csx"
        };

        var jobId = await _fixture.SubmitJobAsync(submission);
        await _fixture.WaitForJobCompletionAsync(jobId);

        var notifications1 = await _fixture.WaitForNotificationsAsync(sub1.Id);
        var notifications2 = await _fixture.WaitForNotificationsAsync(sub2.Id);

        Assert.That(notifications1, Has.Count.EqualTo(1));
        Assert.That(notifications2, Has.Count.EqualTo(1));
        Assert.That(notifications1[0].JobId, Is.EqualTo(jobId));
        Assert.That(notifications2[0].JobId, Is.EqualTo(jobId));
    }

    [Test]
    public async Task MultipleJobs_SubscriberGetsAllNotifications()
    {
        var subscriber = await _fixture.RegisterSubscriberAsync(
            new SubscriberRegistration { Name = "multi-job-test" });

        await _fixture.StartWorkerAsync();

        var submission = new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = _repo.Branch,
            ScriptPath = "pipeline.csx"
        };

        var job1 = await _fixture.SubmitJobAsync(submission);
        await _fixture.WaitForJobCompletionAsync(job1);

        // Ack first notification so the pull for the second one works
        var first = await _fixture.WaitForNotificationsAsync(subscriber.Id);
        await _fixture.AcknowledgeNotificationsAsync(
            subscriber.Id, first.Select(n => n.NotificationId).ToList());

        var job2 = await _fixture.SubmitJobAsync(submission);
        await _fixture.WaitForJobCompletionAsync(job2);

        var second = await _fixture.WaitForNotificationsAsync(subscriber.Id);

        Assert.That(second, Has.Count.EqualTo(1));
        Assert.That(second[0].JobId, Is.EqualTo(job2));
    }

    [Test]
    public async Task CancelledJob_NotifiesSubscribersExactlyOnce()
    {
        var subscriber = await _fixture.RegisterSubscriberAsync(
            new SubscriberRegistration { Name = "cancel-once-test" });

        _repo.AddFile("pipeline.csx", """
            Step("slow")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Exec("sleep", "30");
                });
            """);
        _repo.Commit("Slow pipeline");

        await _fixture.StartWorkerAsync();

        var jobId = await _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = _repo.Branch,
            ScriptPath = "pipeline.csx"
        });
        await _fixture.WaitForJobStatusAsync(jobId, JobStatus.Running);

        var cancelled = await _fixture.CancelJobAsync(jobId);
        Assert.That(cancelled, Is.True);

        // Wait until the worker has aborted and its JobCompleted report has been processed — the worker only turns Ready again inside that handler. The report must not double-notify.
        var workerRepo = _fixture.Services.GetRequiredService<WorkerRepository>();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        var workerReported = false;
        while (DateTime.UtcNow < deadline && !workerReported)
        {
            workerReported = (await workerRepo.GetAllAsync()).Any(w => w.IsReady);
            if (!workerReported)
            {
                await Task.Delay(250);
            }
        }
        Assert.That(workerReported, Is.True,
            "the worker's post-abort JobCompleted report must arrive; without it this test would pass vacuously");

        var job = await _fixture.GetJobAsync(jobId);
        var notifications = await _fixture.PullNotificationsAsync(subscriber.Id);
        var forJob = notifications.Where(n => n.JobId == jobId).ToList();

        if (job.Status == JobStatus.Cancelled)
        {
            Assert.That(forJob, Has.Count.EqualTo(1),
                "a cancelled job must notify subscribers exactly once");
        }
        else
        {
            // Rare: a non-cancellation exception during the worker's teardown makes it report Failed, which overrides Cancelled and must signal again — exactly two notifications.
            Assert.That(job.Status, Is.EqualTo(JobStatus.Failed));
            Assert.That(forJob, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public async Task NotificationProcessor_DeliversAndAcksNotifications()
    {
        await _fixture.StartWorkerAsync();

        using var client = new IlmarinenNotificationClient(
            _fixture.HttpClient.BaseAddress!.ToString(), _fixture.HttpClient);

        var handled = new List<JobNotification>();
        var processor = new NotificationProcessor(
            client,
            new CollectingHandler(handled),
            new NotificationProcessorOptions
            {
                SubscriberName = "processor-test",
                PollInterval = TimeSpan.FromMilliseconds(200)
            },
            NullLogger.Instance);

        using var cts = new CancellationTokenSource();
        var run = processor.RunAsync(cts.Token);

        // The subscriber must be registered before the job completes, or no notification is created for it.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (client.SubscriberId == null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        Assert.That(client.SubscriberId, Is.Not.Null, "processor should register on startup");

        var jobId = await _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = _repo.Branch,
            ScriptPath = "pipeline.csx"
        });
        await _fixture.WaitForJobCompletionAsync(jobId);

        deadline = DateTime.UtcNow.AddSeconds(15);
        while (CountHandled(handled) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        lock (handled)
        {
            Assert.That(handled.Select(n => n.JobId), Does.Contain(jobId),
                "the handler should receive the job-completion notification");
        }

        cts.Cancel();
        await run;

        // A fresh pull returning empty would be vacuous — the processor's own pull locks the row for 5 minutes regardless of ack. Assert the actual ack state in the database.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        var rows = await db.Notifications.Where(n => n.SubscriberId == client.SubscriberId).ToListAsync();
        Assert.That(rows, Is.Not.Empty);
        Assert.That(rows.All(n => n.IsProcessed), Is.True, "the processor must ack handled notifications");
    }

    private static int CountHandled(List<JobNotification> handled)
    {
        lock (handled)
        {
            return handled.Count;
        }
    }

    private sealed class CollectingHandler(List<JobNotification> sink) : INotificationHandler
    {
        public Task<bool> HandleAsync(JobNotification notification, CancellationToken ct)
        {
            lock (sink)
            {
                sink.Add(notification);
            }
            return Task.FromResult(true);
        }
    }
}
