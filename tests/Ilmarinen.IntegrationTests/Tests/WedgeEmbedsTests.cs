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
            MinWorkerPriority = WorkerPriority.High
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
            HighestAvailableWorkerPriority = WorkerPriority.Medium,
            Jobs = Enumerable.Range(0, 50).Select(_ => Job(repoUrl: huge, gitRef: huge, pipelineName: huge)).ToList(),
            PausedWorkers = []
        };

        var embed = WedgeEmbeds.Wedged(report, LinkTo);

        Assert.That(embed.Length, Is.LessThanOrEqualTo(EmbedBuilder.MaxEmbedLength));
        Assert.That(embed.Fields.Length, Is.LessThanOrEqualTo(EmbedBuilder.MaxFieldCount));
        Assert.That(embed.Fields.All(f => f.Name.Length <= EmbedFieldBuilder.MaxFieldNameLength && f.Value.Length <= EmbedFieldBuilder.MaxFieldValueLength), Is.True);
    }

    [TestCase("")]
    [TestCase("   ")]
    public void Wedged_BlankPipelineName_NamesTheJobByItsRepository(string blankName)
    {
        var job = Job(pipelineName: blankName);
        var report = new UnsatisfiableJobsReport { Jobs = [job], PausedWorkers = [] };

        var embed = WedgeEmbeds.Wedged(report, LinkTo);

        Assert.That(embed.Fields.Single().Name, Is.EqualTo(job.RepoUrl));
    }

    [Test]
    public void Wedged_WithAndWithoutLinks_EveryJobCanBeFound()
    {
        var job = Job(pipelineName: "nightly");
        var report = new UnsatisfiableJobsReport { Jobs = [job], PausedWorkers = [] };

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

        var noWorker = WedgeEmbeds.Wedged(new UnsatisfiableJobsReport { HighestAvailableWorkerPriority = null, Jobs = jobs, PausedWorkers = [] }, LinkTo);
        var lowPriority = WedgeEmbeds.Wedged(new UnsatisfiableJobsReport { HighestAvailableWorkerPriority = WorkerPriority.Medium, Jobs = jobs, PausedWorkers = [] }, LinkTo);

        Assert.That(noWorker.Description, Is.Not.EqualTo(lowPriority.Description));
    }

    [Test]
    public void Wedged_PausedWorkersThatCouldTakeThem_AreNamed()
    {
        var jobs = new[] { Job() };

        var none = WedgeEmbeds.Wedged(new UnsatisfiableJobsReport { Jobs = jobs, PausedWorkers = [] }, LinkTo);
        var paused = WedgeEmbeds.Wedged(new UnsatisfiableJobsReport { Jobs = jobs, PausedWorkers = ["builder-a", "builder-b"] }, LinkTo);

        Assert.That(paused.Description, Is.Not.EqualTo(none.Description));
        Assert.That(paused.Description, Does.Contain("builder-a").And.Contain("builder-b"));
    }

    [Test]
    public void Wedged_ManyPausedWorkersWithHugeNames_StaysWithinDiscordLimits()
    {
        var report = new UnsatisfiableJobsReport { Jobs = [Job()], PausedWorkers = Enumerable.Range(0, 500).Select(i => new string('w', 5000)).ToList() };

        var embed = WedgeEmbeds.Wedged(report, LinkTo);

        Assert.That(embed.Description.Length, Is.LessThanOrEqualTo(EmbedBuilder.MaxDescriptionLength));
    }
}
