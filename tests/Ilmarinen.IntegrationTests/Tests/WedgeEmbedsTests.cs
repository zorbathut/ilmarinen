using Discord;
using Ilmarinen.DiscordBot;
using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Responses;
using NUnit.Framework;
using System.Linq;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// The stuck-jobs alert has to be postable whatever the queued jobs look like: Discord rejects an oversized or malformed embed, and a rejected alert is retried every poll without ever going out.
/// </summary>
[TestFixture]
public class WedgeEmbedsTests
{
    private static JobInfo Job(string repoUrl = "https://example.invalid/repo.git", string gitRef = "main", string? pipelineName = null)
    {
        return new JobInfo
        {
            Id = Guid.NewGuid(),
            Status = JobStatus.Queued,
            RepoUrl = repoUrl,
            Ref = gitRef,
            ScriptPath = "pipeline.csx",
            PipelineName = pipelineName,
            MinWorkerPriority = new PriorityRef(Guid.NewGuid(), "High"),
            Priority = new PriorityRef(Guid.NewGuid(), "Default")
        };
    }

    private static string? LinkTo(Guid jobId)
    {
        return $"https://ci.example.invalid/jobs/{jobId}";
    }

    [Test]
    public void Wedged_ManyJobsWithHugeText_StaysWithinDiscordLimits()
    {
        var huge = new string('x', 5000);
        var report = new UnsatisfiableJobsReport
        {
            HighestAvailableWorkerPriority = new PriorityRef(Guid.NewGuid(), "Medium"),
            Jobs = Enumerable.Range(0, 50).Select(_ => Job(repoUrl: huge, gitRef: huge, pipelineName: huge)).ToList(),
            NoRoomJobs = [], PausedWorkers = [], InUseWorkers = [], UnreachableWorkers = []
        };

        var embed = WedgeEmbeds.Wedged(report, LinkTo);

        Assert.That(embed.Length, Is.LessThanOrEqualTo(EmbedBuilder.MaxEmbedLength));
        Assert.That(embed.Fields.Length, Is.LessThanOrEqualTo(EmbedBuilder.MaxFieldCount));
        Assert.That(embed.Fields.All(f => f.Name.Length <= EmbedFieldBuilder.MaxFieldNameLength && f.Value.Length <= EmbedFieldBuilder.MaxFieldValueLength), Is.True);
    }

    [Test]
    public void Wedged_PriorityNames_AreOperatorText_SoTheyCantFormatOrRunLong()
    {
        var name = "**loud** " + new string('x', 500);
        var job = Job() with { MinWorkerPriority = new PriorityRef(Guid.NewGuid(), name) };
        var report = new UnsatisfiableJobsReport { HighestAvailableWorkerPriority = new PriorityRef(Guid.NewGuid(), name), Jobs = [job], NoRoomJobs = [], PausedWorkers = [], InUseWorkers = [], UnreachableWorkers = [] };

        var embed = WedgeEmbeds.Wedged(report, LinkTo);

        Assert.That(embed.Description, Does.Not.Contain("**loud**"));
        Assert.That(embed.Fields.Single().Value, Does.Not.Contain("**loud**"));
        Assert.That(embed.Description, Does.Not.Contain(new string('x', 500)));
    }

    [TestCase("")]
    [TestCase("   ")]
    public void Wedged_BlankPipelineName_NamesTheJobByItsRepository(string blankName)
    {
        var job = Job(pipelineName: blankName);
        var report = new UnsatisfiableJobsReport { Jobs = [job], NoRoomJobs = [], PausedWorkers = [], InUseWorkers = [], UnreachableWorkers = [] };

        var embed = WedgeEmbeds.Wedged(report, LinkTo);

        Assert.That(embed.Fields.Single().Name, Is.EqualTo(job.RepoUrl));
    }

    [Test]
    public void Wedged_WithAndWithoutLinks_EveryJobCanBeFound()
    {
        var job = Job(pipelineName: "nightly");
        var report = new UnsatisfiableJobsReport { Jobs = [job], NoRoomJobs = [], PausedWorkers = [], InUseWorkers = [], UnreachableWorkers = [] };

        var linked = WedgeEmbeds.Wedged(report, LinkTo);
        var unlinked = WedgeEmbeds.Wedged(report, _ => null);

        Assert.That(linked.Fields.Single().Value, Does.Contain(LinkTo(job.Id)));
        Assert.That(unlinked.Fields.Single().Value, Does.Contain(job.Id.ToString()),
            "without a link the job ID is the only way to look the job up");
    }

    [Test]
    public void Wedged_NoWorkerAvailable_ReadsDifferentlyFromTooLowAPriority()
    {
        var jobs = new[] { Job() };

        var noWorker = WedgeEmbeds.Wedged(new UnsatisfiableJobsReport { HighestAvailableWorkerPriority = null, Jobs = jobs, NoRoomJobs = [], PausedWorkers = [], InUseWorkers = [], UnreachableWorkers = [] }, LinkTo);
        var lowPriority = WedgeEmbeds.Wedged(new UnsatisfiableJobsReport { HighestAvailableWorkerPriority = new PriorityRef(Guid.NewGuid(), "Medium"), Jobs = jobs, NoRoomJobs = [], PausedWorkers = [], InUseWorkers = [], UnreachableWorkers = [] }, LinkTo);

        Assert.That(noWorker.Description, Is.Not.EqualTo(lowPriority.Description));
    }

    [Test]
    public void Wedged_PausedWorkersThatCouldTakeThem_AreNamed()
    {
        var jobs = new[] { Job() };

        var none = WedgeEmbeds.Wedged(new UnsatisfiableJobsReport { Jobs = jobs, NoRoomJobs = [], PausedWorkers = [], InUseWorkers = [], UnreachableWorkers = [] }, LinkTo);
        var paused = WedgeEmbeds.Wedged(new UnsatisfiableJobsReport { Jobs = jobs, NoRoomJobs = [], PausedWorkers = ["builder-a", "builder-b"], InUseWorkers = [], UnreachableWorkers = [] }, LinkTo);

        Assert.That(paused.Description, Is.Not.EqualTo(none.Description));
        Assert.That(paused.Description, Does.Contain("builder-a").And.Contain("builder-b"));
    }

    [Test]
    public void Wedged_InUseWorkersThatCouldTakeThem_AreNamed_ApartFromPausedOnes()
    {
        var jobs = new[] { Job() };

        var paused = WedgeEmbeds.Wedged(new UnsatisfiableJobsReport { Jobs = jobs, NoRoomJobs = [], PausedWorkers = ["desk-a"], InUseWorkers = [], UnreachableWorkers = [] }, LinkTo);
        var inUse = WedgeEmbeds.Wedged(new UnsatisfiableJobsReport { Jobs = jobs, NoRoomJobs = [], PausedWorkers = [], InUseWorkers = ["desk-a"], UnreachableWorkers = [] }, LinkTo);

        Assert.That(inUse.Description, Does.Contain("desk-a"));
        Assert.That(inUse.Description, Is.Not.EqualTo(paused.Description));
    }

    [Test]
    public void Wedged_SleepersThatDidntWake_AreNamed()
    {
        var jobs = new[] { Job() };

        var none = WedgeEmbeds.Wedged(new UnsatisfiableJobsReport { Jobs = jobs, NoRoomJobs = [], PausedWorkers = [], InUseWorkers = [], UnreachableWorkers = [] }, LinkTo);
        var unreachable = WedgeEmbeds.Wedged(new UnsatisfiableJobsReport { Jobs = jobs, NoRoomJobs = [], PausedWorkers = [], InUseWorkers = [], UnreachableWorkers = ["desk-b"] }, LinkTo);

        Assert.That(unreachable.Description, Does.Contain("desk-b"));
        Assert.That(unreachable.Description, Is.Not.EqualTo(none.Description));
    }

    [Test]
    public void Wedged_ManyPausedWorkersWithHugeNames_StaysWithinDiscordLimits()
    {
        var report = new UnsatisfiableJobsReport { Jobs = [Job()], NoRoomJobs = [], PausedWorkers = Enumerable.Range(0, 500).Select(i => new string('w', 5000)).ToList(), InUseWorkers = Enumerable.Range(0, 500).Select(i => new string('u', 5000)).ToList(), UnreachableWorkers = Enumerable.Range(0, 500).Select(i => new string('z', 5000)).ToList() };

        var embed = WedgeEmbeds.Wedged(report, LinkTo);

        Assert.That(embed.Description.Length, Is.LessThanOrEqualTo(EmbedBuilder.MaxDescriptionLength));
    }

    [Test]
    public void Wedged_JobsWithoutRoomOnly_SayItIsDisk_AndListThem()
    {
        var job = Job(pipelineName: "big-build");
        var byPriority = WedgeEmbeds.Wedged(new UnsatisfiableJobsReport { Jobs = [job], NoRoomJobs = [], PausedWorkers = [], InUseWorkers = [], UnreachableWorkers = [] }, LinkTo);
        var byDisk = WedgeEmbeds.Wedged(new UnsatisfiableJobsReport { Jobs = [], NoRoomJobs = [new NoRoomJob { Job = job, NeededBytes = 60L << 30, MostRoomBytes = 40L << 30 }], PausedWorkers = [], InUseWorkers = [], UnreachableWorkers = [] }, LinkTo);

        Assert.That(byDisk.Description, Is.Not.Empty.And.Not.EqualTo(byPriority.Description));
        Assert.That(byDisk.Fields.Select(f => f.Name), Is.EqualTo(new[] { "big-build" }));
        Assert.That(byDisk.Fields.Single().Value, Is.Not.EqualTo(byPriority.Fields.Single().Value));
    }

    [Test]
    public void Wedged_ManyJobsOfBothKinds_StaysWithinDiscordLimits()
    {
        var huge = new string('x', 5000);
        var jobs = Enumerable.Range(0, 30).Select(_ => Job(repoUrl: huge, gitRef: huge, pipelineName: huge)).ToList();
        var report = new UnsatisfiableJobsReport { Jobs = jobs, NoRoomJobs = jobs.Select(j => new NoRoomJob { Job = j, NeededBytes = long.MaxValue, MostRoomBytes = null }).ToList(), PausedWorkers = [], InUseWorkers = [], UnreachableWorkers = [] };

        var embed = WedgeEmbeds.Wedged(report, LinkTo);

        Assert.That(embed.Length, Is.LessThanOrEqualTo(EmbedBuilder.MaxEmbedLength));
        Assert.That(embed.Fields, Has.Length.LessThanOrEqualTo(EmbedBuilder.MaxFieldCount));
    }
}
