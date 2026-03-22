using Ilmarinen.Models;
using NUnit.Framework;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Core.Tests;

[TestFixture]
public class StepBuilderTests
{
    [Test]
    public void BasicStep_CreatesStep()
    {
        var step = new StepBuilder("build")
            .Image("dotnet/sdk:8.0")
            .Run(ctx => Task.CompletedTask);

        Assert.That(step.Name, Is.EqualTo("build"));
        Assert.That(step.ImageResolver().Reference, Is.EqualTo("dotnet/sdk:8.0"));
        Assert.That(step.Action, Is.Not.Null);
    }

    [Test]
    public void Step_WithoutImage_Throws()
    {
        var builder = new StepBuilder("build");

        Assert.Throws<InvalidOperationException>(() =>
            builder.Run(ctx => Task.CompletedTask));
    }

    [Test]
    public void StepBuilder_WithEmptyName_Throws()
    {
        Assert.Throws<ArgumentException>(() => new StepBuilder(""));
        Assert.Throws<ArgumentException>(() => new StepBuilder("   "));
    }

    [Test]
    public void StepBuilder_WithEmptyImage_Throws()
    {
        var builder = new StepBuilder("build");
        Assert.Throws<ArgumentException>(() => builder.Image(""));
        Assert.Throws<ArgumentException>(() => builder.Image("   "));
    }

    [Test]
    public void Step_WithSyncAction_Works()
    {
        var step = new StepBuilder("build")
            .Image("alpine")
            .Run(ctx => { });

        Assert.That(step.Action, Is.Not.Null);
    }
}
