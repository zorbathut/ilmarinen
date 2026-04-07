using System;
using Ilmarinen.IntegrationTests.Fixtures;
using System;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using NUnit.Framework;
using System.Linq;
using System.Threading.Tasks;

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

        // After ack, pulling again should return empty (notifications are processed)
        // Need to wait for lock to expire or pull should skip processed ones
        // Actually, ack marks them as processed, so next pull should be empty
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
}
