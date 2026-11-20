using Ilmarinen.Database.Entities;
using Ilmarinen.Database;
using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
public class JobDurationEstimateTests
{
    [Test]
    public void EstimateDuration_NoRuns_HasNoEstimate()
    {
        Assert.That(JobRepository.EstimateDuration([]), Is.Null);
    }

    [Test]
    public void EstimateDuration_OnlyTheSetupRun_HasNoEstimate()
    {
        Assert.That(JobRepository.EstimateDuration([Minutes(30)]), Is.Null);
    }

    [Test]
    public void EstimateDuration_LeavesOutTheFirstRun()
    {
        Assert.That(JobRepository.EstimateDuration([Minutes(3), Minutes(30)]), Is.EqualTo(Minutes(3)));
    }

    [Test]
    public void EstimateDuration_OddCount_TakesTheMiddleRun()
    {
        Assert.That(JobRepository.EstimateDuration([Minutes(4), Minutes(2), Minutes(9), Minutes(30)]), Is.EqualTo(Minutes(4)));
    }

    [Test]
    public void EstimateDuration_EvenCount_AveragesTheMiddleTwo()
    {
        Assert.That(JobRepository.EstimateDuration([Minutes(100), Minutes(6), Minutes(4), Minutes(2), Minutes(30)]), Is.EqualTo(Minutes(5)));
    }

    [Test]
    public void EstimateDuration_LongHistory_OnlyTheLatestTenCount()
    {
        // Newest first: ten recent runs, then older ones that would drag the median up if they counted
        TimeSpan[] history = [.. Enumerable.Repeat(Minutes(1), 5), .. Enumerable.Repeat(Minutes(9), 5), .. Enumerable.Repeat(Minutes(90), 5)];

        Assert.That(JobRepository.EstimateDuration(history), Is.EqualTo(Minutes(5)));
    }

    [TestFixture]
    [Category("Integration")]
    public class FromHistory
    {
        private static IntegrationTestFixture _fixture = null!;
        private string _repoUrl = null!;
        private readonly Guid _workerId = Guid.CreateVersion7();
        private DateTime _clock;

        // One server for the whole class; each test gets its own repository URL and worker, so their histories never meet even when they run in parallel
        [OneTimeSetUp]
        public static async Task OneTimeSetUp()
        {
            _fixture = new IntegrationTestFixture();
            await _fixture.SetupAsync();
        }

        [OneTimeTearDown]
        public static async Task OneTimeTearDown()
        {
            await _fixture.DisposeAsync();
        }

        [SetUp]
        public void SetUp()
        {
            _repoUrl = $"https://example.invalid/{Guid.NewGuid():N}.git";
            _clock = DateTime.UtcNow.AddDays(-1);
        }

        [Test]
        public async Task TwoSuccesses_TheSecondIsTheEstimate()
        {
            await SeedRunsAsync(JobStatus.Success, [40, 3]);
            var running = await SeedRunningAsync();

            Assert.That(await EstimateForAsync(running), Is.EqualTo(Minutes(3)));
        }

        [Test]
        public async Task OneSuccess_HasNoEstimate()
        {
            await SeedRunsAsync(JobStatus.Success, [40]);
            var running = await SeedRunningAsync();

            Assert.That(await EstimateForAsync(running), Is.Null);
        }

        [Test]
        public async Task ThreeSuccesses_MediansTheTwoAfterSetup()
        {
            await SeedRunsAsync(JobStatus.Success, [40, 2, 6]);
            var running = await SeedRunningAsync();

            Assert.That(await EstimateForAsync(running), Is.EqualTo(Minutes(4)));
        }

        [Test]
        public async Task TwelveSuccesses_OnlyTheLatestTenCount()
        {
            // The two oldest would drag the median up if they counted
            await SeedRunsAsync(JobStatus.Success, [90, 90, 1, 1, 1, 1, 1, 9, 9, 9, 9, 9]);
            var running = await SeedRunningAsync();

            Assert.That(await EstimateForAsync(running), Is.EqualTo(Minutes(5)));
        }

        [Test]
        public async Task FailedAndCancelledRuns_DoNotCount()
        {
            await SeedRunsAsync(JobStatus.Success, [40, 3]);
            await SeedRunsAsync(JobStatus.Failed, [1, 1, 1]);
            await SeedRunsAsync(JobStatus.Cancelled, [50, 50]);
            var running = await SeedRunningAsync();

            Assert.That(await EstimateForAsync(running), Is.EqualTo(Minutes(3)));
        }

        [Test]
        public async Task FirstRunFailed_TheFirstSuccessIsStillTheOneLeftOut()
        {
            await SeedRunsAsync(JobStatus.Failed, [40]);
            await SeedRunsAsync(JobStatus.Success, [5, 3]);
            var running = await SeedRunningAsync();

            Assert.That(await EstimateForAsync(running), Is.EqualTo(Minutes(3)));
        }

        [Test]
        public async Task SeveralRunningJobs_EachGetsItsOwnTriplesEstimate()
        {
            await SeedRunsAsync(JobStatus.Success, [40, 3]);
            await SeedRunsAsync(JobStatus.Success, [40, 7], scriptPath: "other.csx");
            var first = await SeedRunningAsync();
            var second = await SeedRunningAsync();
            var other = await SeedRunningAsync(scriptPath: "other.csx");

            using var scope = _fixture.Services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<JobRepository>();
            var jobs = (await repo.GetAllAsync()).Where(j => j.RepoUrl == _repoUrl).ToList();
            var estimates = await repo.GetDurationEstimatesAsync(jobs);

            Assert.That(estimates[first], Is.EqualTo(Minutes(3)));
            Assert.That(estimates[second], Is.EqualTo(Minutes(3)));
            Assert.That(estimates[other], Is.EqualTo(Minutes(7)));
            Assert.That(estimates.Keys, Is.EquivalentTo(new[] { first, second, other }));
        }

        [Test]
        public async Task RunsOfOtherWorkersScriptsAndRepos_DoNotCount()
        {
            await SeedRunsAsync(JobStatus.Success, [40, 3]);
            await SeedRunsAsync(JobStatus.Success, [50, 50, 50], workerId: Guid.CreateVersion7());
            await SeedRunsAsync(JobStatus.Success, [50, 50, 50], scriptPath: "other.csx");
            await SeedRunsAsync(JobStatus.Success, [50, 50, 50], repoUrl: _repoUrl + "-other");
            var running = await SeedRunningAsync();

            Assert.That(await EstimateForAsync(running), Is.EqualTo(Minutes(3)));
        }

        [Test]
        public async Task JobsThatAreNotRunning_GetNoEstimate()
        {
            await SeedRunsAsync(JobStatus.Success, [40, 3, 3]);

            using var scope = _fixture.Services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<JobRepository>();
            var finished = (await repo.GetAllAsync()).Where(j => j.RepoUrl == _repoUrl).ToList();
            var estimates = await repo.GetDurationEstimatesAsync(finished);

            Assert.That(finished, Is.Not.Empty);
            Assert.That(estimates, Is.Empty);
        }

        private async Task SeedRunsAsync(JobStatus status, int[] minutes, Guid? workerId = null, string? scriptPath = null, string? repoUrl = null)
        {
            using var scope = _fixture.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
            var priorityId = await StandardPriority.DefaultJobPriorityIdAsync(db);
            foreach (var length in minutes)
            {
                // Runs one after another, oldest first
                var started = _clock;
                _clock = started.AddMinutes(length + 1);
                db.Jobs.Add(NewJob(status, started, started.AddMinutes(length), workerId, scriptPath, repoUrl, priorityId));
            }
            await db.SaveChangesAsync();
        }

        private async Task<Guid> SeedRunningAsync(string? scriptPath = null)
        {
            using var scope = _fixture.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
            var job = NewJob(JobStatus.Running, DateTime.UtcNow.AddMinutes(-1), null, null, scriptPath, null, await StandardPriority.DefaultJobPriorityIdAsync(db));
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            return job.Id;
        }

        private Job NewJob(JobStatus status, DateTime started, DateTime? completed, Guid? workerId, string? scriptPath, string? repoUrl, Guid priorityId)
        {
            return new Job
            {
                Id = Guid.CreateVersion7(),
                Status = status,
                RepoUrl = repoUrl ?? _repoUrl,
                Ref = "master",
                ScriptPath = scriptPath ?? "pipeline.csx",
                WorkerId = workerId ?? _workerId,
                CreatedAt = started,
                StartedAt = started,
                CompletedAt = completed,
                PriorityId = priorityId
            };
        }

        private async Task<TimeSpan?> EstimateForAsync(Guid jobId)
        {
            using var scope = _fixture.Services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<JobRepository>();
            var job = await repo.GetAsync(jobId);
            var estimates = await repo.GetDurationEstimatesAsync([job!]);
            return estimates.TryGetValue(jobId, out var estimate) ? estimate : null;
        }
    }

    private static TimeSpan Minutes(int minutes)
    {
        return TimeSpan.FromMinutes(minutes);
    }
}
