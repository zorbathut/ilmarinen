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
