using Ilmarinen.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Ilmarinen.Core.Tests;

/// <summary>The real reading, on a directory that isn't a mount point, as a workspace root usually isn't.</summary>
[TestFixture]
public class DriveDiskSpaceTests
{
    [Test]
    public void Read_AnyDirectory_GivesItsFilesystemsSpace()
    {
        var reading = new DriveDiskSpace(NullLogger<DriveDiskSpace>.Instance).Read(TestContext.CurrentContext.WorkDirectory);

        Assert.That(reading, Is.Not.Null);
        Assert.That(reading!.TotalBytes, Is.GreaterThan(0));
        Assert.That(reading.FreeBytes, Is.InRange(0, reading.TotalBytes));
    }
}
