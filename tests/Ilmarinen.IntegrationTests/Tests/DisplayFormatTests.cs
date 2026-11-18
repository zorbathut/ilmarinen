using Ilmarinen.Server.Components;
using NUnit.Framework;

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
}
