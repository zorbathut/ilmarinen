using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Components;
using NUnit.Framework;
using System;
using System.Linq;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
public class DisplayFormatTests
{
    [TestCase(0L, "0 B")]
    [TestCase(1023L, "1023 B")]
    [TestCase(1024L, "1.0 KB")]
    [TestCase(1536L, "1.5 KB")]
    [TestCase(1024L * 1024, "1.0 MB")]
    [TestCase(1024L * 1024 * 1024, "1.0 GB")]
    [TestCase(5L * 1024 * 1024 * 1024 * 1024, "5120.0 GB")]
    public void Size_PicksTheLargestUnitBelowTheValue(long bytes, string expected)
    {
        Assert.That(DisplayFormat.Size(bytes), Is.EqualTo(expected));
    }

    [TestCase(0, "0s")]
    [TestCase(59, "59s")]
    [TestCase(60, "1m 0s")]
    [TestCase(3599, "59m 59s")]
    [TestCase(3600, "1h 0m")]
    [TestCase(90061, "25h 1m")]
    public void Duration_PicksUnitsByMagnitude(int seconds, string expected)
    {
        Assert.That(DisplayFormat.Duration(TimeSpan.FromSeconds(seconds)), Is.EqualTo(expected));
    }

    [Test]
    public void JobElapsed_NotStarted_ReadsAsNoDuration()
    {
        var now = DateTime.UtcNow;

        Assert.That(DisplayFormat.JobElapsed(JobAt(started: null, completed: null), now), Is.EqualTo("---"));
    }

    [Test]
    public void JobElapsed_Running_CountsUpToNow()
    {
        var now = DateTime.UtcNow;

        Assert.That(DisplayFormat.JobElapsed(JobAt(started: now.AddSeconds(-90), completed: null), now), Is.EqualTo(DisplayFormat.Duration(TimeSpan.FromSeconds(90))));
    }

    [Test]
    public void JobElapsed_Completed_StopsAtCompletion()
    {
        var now = DateTime.UtcNow;
        var job = JobAt(started: now.AddHours(-2), completed: now.AddHours(-2).AddSeconds(45));

        Assert.That(DisplayFormat.JobElapsed(job, now), Is.EqualTo(DisplayFormat.Duration(TimeSpan.FromSeconds(45))));
    }

    private static JobInfo JobAt(DateTime? started, DateTime? completed)
    {
        return new JobInfo
        {
            Id = Guid.CreateVersion7(),
            Status = JobStatus.Running,
            RepoUrl = "https://example.invalid/repo.git",
            Ref = "master",
            ScriptPath = "pipeline.csx",
            StartedAt = started,
            CompletedAt = completed
        };
    }

    [Test]
    public void MeasuredSize_NotYetMeasuredFailedAndMeasured_AllReadDifferently()
    {
        var texts = new[]
        {
            DisplayFormat.MeasuredSize(false, null),
            DisplayFormat.MeasuredSize(true, null),
            DisplayFormat.MeasuredSize(true, 4096)
        };

        Assert.That(texts, Is.Unique);
        Assert.That(texts[2], Is.EqualTo(DisplayFormat.Size(4096)));
    }

    [Test]
    public void Ago_ReadsDifferentlyAcrossMinutesHoursAndDays()
    {
        var now = DateTimeOffset.UtcNow;
        var ages = new[] { TimeSpan.Zero, TimeSpan.FromMinutes(5), TimeSpan.FromHours(5), TimeSpan.FromDays(5) };

        var texts = ages.Select(age => DisplayFormat.Ago(now - age, now)).ToList();

        Assert.That(texts, Is.Unique);
        Assert.That(texts, Has.All.Not.Empty);
    }

    [Test]
    public void Ago_WithinTheSameUnit_ReadsTheSameShapeWithDifferentCounts()
    {
        var now = DateTimeOffset.UtcNow;

        var two = DisplayFormat.Ago(now - TimeSpan.FromDays(2), now);
        var three = DisplayFormat.Ago(now - TimeSpan.FromDays(3), now);

        Assert.That(two, Is.Not.EqualTo(three));
        Assert.That(two.Replace("2", "3"), Is.EqualTo(three));
    }

    [Test]
    public void Ago_FutureTime_ReadsLikeNow()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.That(DisplayFormat.Ago(now + TimeSpan.FromMinutes(3), now), Is.EqualTo(DisplayFormat.Ago(now, now)));
    }
}
