using Ilmarinen.Server.Components;
using NUnit.Framework;
using System;
using System.Linq;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
public class ShortIdTests
{
    [Test]
    public void From_IdsCreatedInTheSameMillisecond_AreDistinct()
    {
        var now = DateTimeOffset.UtcNow;
        var ids = Enumerable.Range(0, 100).Select(_ => Guid.CreateVersion7(now)).ToList();

        var shortIds = ids.Select(ShortId.From).ToList();

        Assert.That(shortIds.Distinct().Count(), Is.EqualTo(ids.Count));
    }

    [Test]
    public void From_IsTheEndOfTheFullId()
    {
        var id = Guid.CreateVersion7();

        Assert.That(id.ToString(), Does.EndWith(ShortId.From(id)));
        Assert.That(ShortId.From(id), Has.Length.EqualTo(8));
    }
}
