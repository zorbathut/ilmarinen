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
            Jobs = Enumerable.Range(0, 50).Select(_ => Job(repoUrl: huge, gitRef: huge, pipelineName: huge)).ToList()
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
        var report = new UnsatisfiableJobsReport { Jobs = [job] };

        var embed = WedgeEmbeds.Wedged(report, LinkTo);

        Assert.That(embed.Fields.Single().Name, Is.EqualTo(job.RepoUrl));
    }

    [Test]
    public void Wedged_WithAndWithoutLinks_EveryJobCanBeFound()
    {
        var job = Job(pipelineName: "nightly");
        var report = new UnsatisfiableJobsReport { Jobs = [job] };

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

        var noWorker = WedgeEmbeds.Wedged(new UnsatisfiableJobsReport { HighestAvailableWorkerPriority = null, Jobs = jobs }, LinkTo);
        var lowPriority = WedgeEmbeds.Wedged(new UnsatisfiableJobsReport { HighestAvailableWorkerPriority = WorkerPriority.Medium, Jobs = jobs }, LinkTo);

        Assert.That(noWorker.Description, Is.Not.EqualTo(lowPriority.Description));
    }
}
