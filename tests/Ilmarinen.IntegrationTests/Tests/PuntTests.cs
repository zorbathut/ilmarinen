using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http.Json;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// Punting a running job back into the queue, driven through the hub: the run is stopped first and the job queued again only once its worker says so, its log kept and carried on, what belonged to the stopped run dropped, and nothing about the punt outliving the run it was for.
/// </summary>
[TestFixture]
[Category("Integration")]
public class PuntTests
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
        get
        {
            return _fixture.Services.GetRequiredService<JobScheduler>();
        }
    }

    [Test]
    public async Task Punt_StopsTheRun_ThenQueuesTheJobAgain_KeepingItsLog_AndDroppingTheRest()
    {
        var worker = await DirectedWorker.ReadyAsync(_fixture, "punt-worker", "conn-punt");
        var (jobId, first) = await StartAsync(worker);
        await SendLogAsync(worker, jobId, first, 1, "first run, line one");
        await SendLogAsync(worker, jobId, first, 2, "first run, line two");
        await worker.Hub.StreamMetrics(new JobMetricsChunk { JobId = jobId, AssignmentId = first, SequenceNumber = 0, Content = "{}\n", Timestamp = DateTime.UtcNow });
        await worker.Hub.ReportCommit(jobId, "0123456789abcdef0123456789abcdef01234567");
        await SaveArtifactAsync(jobId);

        var answer = await PuntAsync(jobId, force: true);

        Assert.That(answer.Status, Is.EqualTo(PuntStatus.Requested));
        var punting = await _fixture.GetJobAsync(jobId);
        Assert.That(punting.Status, Is.EqualTo(JobStatus.Running), "the job stays its worker's until the run has stopped");
        Assert.That(punting.PuntRequested, Is.True);
        Assert.That((await worker.SentAsync()).CancelledJobs, Is.EqualTo(new[] { jobId.ToString() }));

        // The run keeps writing until it has stopped: what it says about its cancellation comes after the punt's line.
        await SendLogAsync(worker, jobId, first, 3, "first run, cancelled");
        await worker.ReportAsync(DirectedWorker.Completed(jobId, first, JobStatus.Cancelled));
        await worker.ReportAsync(worker.Idle());

        // The only worker takes it again, under a new assignment.
        var rerun = await _fixture.GetJobAsync(jobId);
        var second = (await worker.SentAsync()).Assignments.Last(a => a.Id == jobId).AssignmentId;
        Assert.That(second, Is.Not.EqualTo(first));
        Assert.That(rerun.Status, Is.EqualTo(JobStatus.Running));
        Assert.That(rerun.PuntRequested, Is.False);
        Assert.That(rerun.Commit, Is.Null, "the stopped run's commit isn't the rerun's");
        Assert.That(await _fixture.GetArtifactsAsync(jobId), Is.Empty);
        using (var scope = _fixture.Services.CreateScope())
        {
            var metrics = scope.ServiceProvider.GetRequiredService<JobMetricsRepository>();
            Assert.That(await metrics.GetContentAsync(jobId), Is.Empty);
        }

        // The rerun numbers its log from 1 again, and reads after the punt's lines; the stopped run's late output is dropped.
        await SendLogAsync(worker, jobId, second, 1, "second run, line one");
        await SendLogAsync(worker, jobId, first, 3, "first run, late");

        var lines = await ReadLogAsync(jobId);
        Assert.That(lines.Count, Is.EqualTo(6), string.Join("\n", lines));
        Assert.That(lines[0], Does.Contain("first run, line one"));
        Assert.That(lines[1], Does.Contain("first run, line two"));
        Assert.That(lines[2], Does.Contain("[ilmarinen]"), "the punt's request");
        Assert.That(lines[3], Does.Contain("first run, cancelled"));
        Assert.That(lines[4], Does.Contain("[ilmarinen]"), "the requeue");
        Assert.That(lines[5], Does.Contain("second run, line one"));

        // Likewise its metrics: the stopped run's late batch is dropped, the rerun's kept.
        await worker.Hub.StreamMetrics(new JobMetricsChunk { JobId = jobId, AssignmentId = first, SequenceNumber = 1, Content = "{\"late\":1}\n", Timestamp = DateTime.UtcNow });
        await worker.Hub.StreamMetrics(new JobMetricsChunk { JobId = jobId, AssignmentId = second, SequenceNumber = 0, Content = "{\"rerun\":1}\n", Timestamp = DateTime.UtcNow });
        using (var scope = _fixture.Services.CreateScope())
        {
            var content = await scope.ServiceProvider.GetRequiredService<JobMetricsRepository>().GetContentAsync(jobId);
            Assert.That(content, Does.Contain("rerun"));
            Assert.That(content, Does.Not.Contain("late"));
        }
    }

    [Test]
    public async Task Punt_WithNowhereElseToGo_IsRefusedUnlessForced()
    {
        var worker = await DirectedWorker.ReadyAsync(_fixture, "punt-alone-worker", "conn-punt-alone");
        var (jobId, _) = await StartAsync(worker);

        var answer = await PuntAsync(jobId, force: false);

        Assert.That(answer.Status, Is.EqualTo(PuntStatus.WouldComeBack));
        Assert.That(answer.Forecast!.Outcome, Is.EqualTo(PuntOutcome.ComesBack));
        Assert.That(answer.Forecast!.WorkerId, Is.EqualTo(worker.WorkerId));
        Assert.That((await _fixture.GetJobAsync(jobId)).PuntRequested, Is.False);
        Assert.That((await worker.SentAsync()).CancelledJobs, Is.Empty);
    }

    [Test]
    public async Task Punt_WithAnotherWorkerIdle_GoesThere()
    {
        var busy = await DirectedWorker.ReadyAsync(_fixture, "punt-busy-worker", "conn-punt-busy");
        var (jobId, first) = await StartAsync(busy);
        var idle = await DirectedWorker.ReadyAsync(_fixture, "punt-idle-worker", "conn-punt-idle");

        var answer = await PuntAsync(jobId, force: false);
        Assert.That(answer.Status, Is.EqualTo(PuntStatus.Requested));
        Assert.That(answer.Forecast!.Outcome, Is.EqualTo(PuntOutcome.GoesElsewhere));
        Assert.That(answer.Forecast!.WorkerId, Is.EqualTo(idle.WorkerId));

        await busy.ReportAsync(DirectedWorker.Completed(jobId, first, JobStatus.Cancelled));

        Assert.That((await idle.SentAsync()).Assignments.Select(a => a.Id), Is.EqualTo(new[] { jobId }));
    }

    [Test]
    public async Task Punt_ThatTheRunBeat_CompletesTheJob_AndSaysSoBeforeItEnds()
    {
        var worker = await DirectedWorker.ReadyAsync(_fixture, "punt-late-worker", "conn-punt-late");
        var (jobId, first) = await StartAsync(worker);
        await PuntAsync(jobId, force: true);

        await worker.ReportAsync(DirectedWorker.Completed(jobId, first, JobStatus.Success));

        var job = await _fixture.GetJobAsync(jobId);
        Assert.That(job.Status, Is.EqualTo(JobStatus.Success));
        Assert.That(job.PuntRequested, Is.False);
        var lines = await ReadLogAsync(jobId);
        Assert.That(lines.Count, Is.EqualTo(2), string.Join("\n", lines));
        Assert.That(lines.Distinct().Count(), Is.EqualTo(2), "the request, then that it came too late");
    }

    [Test]
    public async Task Punt_Repeated_DoesNothingMore()
    {
        var worker = await DirectedWorker.ReadyAsync(_fixture, "punt-twice-worker", "conn-punt-twice");
        var (jobId, _) = await StartAsync(worker);

        Assert.That((await PuntAsync(jobId, force: true)).Status, Is.EqualTo(PuntStatus.Requested));
        Assert.That((await PuntAsync(jobId, force: true)).Status, Is.EqualTo(PuntStatus.AlreadyRequested));

        Assert.That((await worker.SentAsync()).CancelledJobs, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Punt_OfARunThatHasEnded_IsRefused_AndLeavesTheNextRunAlone()
    {
        var worker = await DirectedWorker.ReadyAsync(_fixture, "punt-stale-worker", "conn-punt-stale");
        var (jobId, first) = await StartAsync(worker);
        var firstStarted = (await _fixture.GetJobAsync(jobId)).StartedAt!.Value;
        await PuntAsync(jobId, force: true);
        await worker.ReportAsync(DirectedWorker.Completed(jobId, first, JobStatus.Cancelled));
        await worker.ReportAsync(worker.Idle());
        Assert.That((await _fixture.GetJobAsync(jobId)).Status, Is.EqualTo(JobStatus.Running), "running again");

        var stale = await Scheduler.PuntJobAsync(jobId, firstStarted, force: true, "a test");

        Assert.That(stale!.Status, Is.EqualTo(PuntStatus.Stale));
        Assert.That((await _fixture.GetJobAsync(jobId)).PuntRequested, Is.False);
        Assert.That((await worker.SentAsync()).CancelledJobs, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Punt_OfAJobNotRunning_IsRefused_AndOfNoJobFindsNothing()
    {
        var jobId = await SubmitAsync();

        Assert.That((await Scheduler.PuntJobAsync(jobId, DateTime.UtcNow, force: true, "a test"))!.Status, Is.EqualTo(PuntStatus.NotRunning));
        Assert.That(await Scheduler.PuntJobAsync(Guid.NewGuid(), DateTime.UtcNow, force: true, "a test"), Is.Null);
    }

    [Test]
    public async Task Cancel_WhilePunting_StandsAgainstTheStoppedRun_AndARealOutcomeStillOverridesIt()
    {
        var worker = await DirectedWorker.ReadyAsync(_fixture, "punt-cancel-worker", "conn-punt-cancel");
        var (jobId, first) = await StartAsync(worker);
        await PuntAsync(jobId, force: true);
        await _fixture.CancelJobAsync(jobId);

        await worker.ReportAsync(DirectedWorker.Completed(jobId, first, JobStatus.Cancelled));
        Assert.That((await _fixture.GetJobAsync(jobId)).Status, Is.EqualTo(JobStatus.Cancelled), "not queued again");

        await worker.ReportAsync(DirectedWorker.Completed(jobId, first, JobStatus.Success));
        var job = await _fixture.GetJobAsync(jobId);
        Assert.That(job.Status, Is.EqualTo(JobStatus.Success));
        Assert.That(job.PuntRequested, Is.False);
    }

    [Test]
    public async Task HandBack_WhilePunting_LeavesNoPuntBehind()
    {
        _fixture.WorkerClients.AssignmentDelay = TimeSpan.FromMilliseconds(200);
        var worker = await DirectedWorker.ReadyAsync(_fixture, "punt-handback-worker", "conn-punt-handback");
        var jobId = await SubmitAsync();
        var assignment = (await worker.SentAsync()).Assignments.Single().AssignmentId;
        await PuntAsync(jobId, force: true);

        await worker.ReportAsync(DirectedWorker.HandBack(jobId, assignment));
        await worker.ReportAsync(worker.Idle());

        var job = await _fixture.GetJobAsync(jobId);
        Assert.That(job.PuntRequested, Is.False, "the punt was for the run that was handed back");
        var second = (await worker.SentAsync()).Assignments.Last().AssignmentId;
        await worker.ReportAsync(worker.Running(jobId, second));
        await worker.ReportAsync(DirectedWorker.Completed(jobId, second, JobStatus.Cancelled));
        Assert.That((await _fixture.GetJobAsync(jobId)).Status, Is.EqualTo(JobStatus.Cancelled), "a cancelled rerun is cancelled, not punted");
    }

    [Test]
    public async Task Resume_WithThePuntedRunStillGoing_TellsTheWorkerToStopIt()
    {
        var worker = await DirectedWorker.ReadyAsync(_fixture, "punt-away-worker", "conn-punt-away-1");
        var (jobId, first) = await StartAsync(worker);
        await worker.Hub.OnDisconnectedAsync(null);
        var answer = await PuntAsync(jobId, force: true);
        Assert.That(answer.WorkerConnected, Is.False);

        await worker.ConnectAsync("conn-punt-away-2");
        var resumed = await worker.ResumeAsync(worker.Running(jobId, first));

        Assert.That(resumed.AbortAssignments, Is.EqualTo(new[] { first }));
        Assert.That((await _fixture.GetJobAsync(jobId)).Status, Is.EqualTo(JobStatus.Running), "until the worker says it has stopped");
    }

    [Test]
    public async Task Resume_AsANewProcessWithoutThePuntedRun_QueuesItAgain_RatherThanFailingIt()
    {
        var worker = await DirectedWorker.ReadyAsync(_fixture, "punt-restart-worker", "conn-punt-restart-1");
        var (jobId, _) = await StartAsync(worker);
        await PuntAsync(jobId, force: true);
        await worker.Hub.OnDisconnectedAsync(null);

        worker.Restart();
        await worker.ConnectAsync("conn-punt-restart-2");
        await worker.ResumeAsync(worker.Idle());

        var job = await _fixture.GetJobAsync(jobId);
        Assert.That(job.Status, Is.Not.EqualTo(JobStatus.Failed));
        Assert.That((await worker.SentAsync()).Assignments.Select(a => a.Id), Is.EqualTo(new[] { jobId }), "queued again, and taken by the only worker");
        Assert.That((await ReadLogAsync(jobId)).Count, Is.EqualTo(2), "the request and the requeue");
    }

    [Test]
    public async Task TheStoppedRunsOutcome_ReplayedDuringTheRerun_LeavesTheRerunAlone()
    {
        var worker = await DirectedWorker.ReadyAsync(_fixture, "punt-replay-worker", "conn-punt-replay-1");
        var (jobId, first) = await StartAsync(worker);
        await PuntAsync(jobId, force: true);
        await worker.ReportAsync(DirectedWorker.Completed(jobId, first, JobStatus.Cancelled));
        await worker.ReportAsync(worker.Idle());
        var second = (await worker.SentAsync()).Assignments.Last().AssignmentId;

        // The acknowledgement was lost: the worker resends the outcome when it reconnects, running the rerun.
        await worker.ConnectAsync("conn-punt-replay-2");
        await worker.ResumeAsync(worker.Running(jobId, second), DirectedWorker.Completed(jobId, first, JobStatus.Cancelled));

        var job = await _fixture.GetJobAsync(jobId);
        Assert.That(job.Status, Is.EqualTo(JobStatus.Running));
        Assert.That(job.PuntRequested, Is.False);
    }

    [Test]
    public async Task LogFromAWorkerNamingNoAssignment_CountsAsTheCurrentRun()
    {
        var worker = await DirectedWorker.ReadyAsync(_fixture, "punt-v1-worker", "conn-punt-v1");
        var (jobId, first) = await StartAsync(worker);
        await PuntAsync(jobId, force: true);
        await worker.ReportAsync(DirectedWorker.Completed(jobId, first, JobStatus.Cancelled));
        await worker.ReportAsync(worker.Idle());

        await worker.Hub.StreamLogs(new LogChunk { JobId = jobId, SequenceNumber = 1, Content = Line("o", "from a v1 worker"), Timestamp = DateTime.UtcNow });
        await worker.Hub.StreamMetrics(new JobMetricsChunk { JobId = jobId, SequenceNumber = 0, Content = "{\"v1\":1}\n", Timestamp = DateTime.UtcNow });

        var lines = await ReadLogAsync(jobId);
        Assert.That(lines.Last(), Does.Contain("from a v1 worker"), "after the punt's lines, not hidden behind them");
        using var scope = _fixture.Services.CreateScope();
        Assert.That(await scope.ServiceProvider.GetRequiredService<JobMetricsRepository>().GetContentAsync(jobId), Does.Contain("v1"));
    }

    [Test]
    public async Task Rest_PuntAnswers202_409_And404()
    {
        var worker = await DirectedWorker.ReadyAsync(_fixture, "punt-rest-worker", "conn-punt-rest");
        var (jobId, _) = await StartAsync(worker);
        var startedAt = (await _fixture.GetJobAsync(jobId)).StartedAt!.Value;

        var refused = await _fixture.HttpClient.PostAsJsonAsync($"/api/jobs/{jobId}/punt", new PuntRequest { StartedAt = startedAt });
        Assert.That(refused.StatusCode, Is.EqualTo(HttpStatusCode.Conflict), "it would come straight back");

        var forecast = await _fixture.HttpClient.GetFromJsonAsync<PuntForecastInfo>($"/api/jobs/{jobId}/punt");
        Assert.That(forecast!.Outcome, Is.EqualTo(PuntOutcome.ComesBack));

        var accepted = await _fixture.HttpClient.PostAsJsonAsync($"/api/jobs/{jobId}/punt", new PuntRequest { StartedAt = startedAt, Force = true });
        Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));

        var missing = await _fixture.HttpClient.PostAsJsonAsync($"/api/jobs/{Guid.NewGuid()}/punt", new PuntRequest { StartedAt = startedAt });
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        // A start time with no zone can't name a run; it is the caller's mistake, not the server's.
        var unzoned = await _fixture.HttpClient.PostAsync($"/api/jobs/{jobId}/punt", new StringContent("{\"startedAt\":\"2026-01-01T00:00:00\"}", Encoding.UTF8, "application/json"));
        Assert.That(unzoned.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Forecast_OfAJobNotRunning_IsRefused()
    {
        var jobId = await SubmitAsync();

        var response = await _fixture.HttpClient.GetAsync($"/api/jobs/{jobId}/punt");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    private async Task<(Guid JobId, Guid AssignmentId)> StartAsync(DirectedWorker worker)
    {
        var jobId = await SubmitAsync();
        var assignment = (await worker.SentAsync()).Assignments.Last(a => a.Id == jobId).AssignmentId;
        await worker.ReportAsync(worker.Running(jobId, assignment));
        return (jobId, assignment);
    }

    private async Task<PuntAnswer> PuntAsync(Guid jobId, bool force)
    {
        var startedAt = (await _fixture.GetJobAsync(jobId)).StartedAt!.Value;
        return (await Scheduler.PuntJobAsync(jobId, startedAt, force, "a test"))!;
    }

    private static string Line(string type, string text)
    {
        return System.Text.Json.JsonSerializer.Serialize(new { t = type, d = text, ts = 0 }) + "\n";
    }

    private static async Task SendLogAsync(DirectedWorker worker, Guid jobId, Guid assignmentId, int sequence, string text)
    {
        await worker.Hub.StreamLogs(new LogChunk { JobId = jobId, AssignmentId = assignmentId, SequenceNumber = sequence, Content = Line("o", text), Timestamp = DateTime.UtcNow });
    }

    private async Task SaveArtifactAsync(Guid jobId)
    {
        using var scope = _fixture.Services.CreateScope();
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("artifact"));
        await scope.ServiceProvider.GetRequiredService<ArtifactRepository>().SaveAsync(jobId, "out.txt", content.Length, content);
    }

    /// <summary>The job's log as a reader pages through it: in sequence order, a repeated number read once.</summary>
    private async Task<IReadOnlyList<string>> ReadLogAsync(Guid jobId)
    {
        await _fixture.Services.GetRequiredService<LogStreamService>().FlushJobAsync(jobId);
        using var scope = _fixture.Services.CreateScope();
        var chunks = await scope.ServiceProvider.GetRequiredService<JobLogRepository>().GetChunksAsync(jobId, 0, 1000);
        var seen = new HashSet<int>();
        return chunks.Where(c => seen.Add(c.SequenceNumber)).Select(c => c.Content.Trim()).ToList();
    }

    private Task<Guid> SubmitAsync()
    {
        return Scheduler.EnqueueJobAsync(new JobSubmission
        {
            RepoUrl = "/nonexistent/repo",
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });
    }
}
