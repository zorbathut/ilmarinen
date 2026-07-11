using Ilmarinen.Scripting;
using NUnit.Framework;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Core.Tests;

[TestFixture]
public class ScriptStepBuilderTests
{
    [Test]
    public void BasicStep_CreatesStepAndCollectsIt()
    {
        var globals = new ScriptGlobals();

        var step = globals.Step("build")
            .Image("dotnet/sdk:8.0")
            .Run(ctx => Task.CompletedTask);

        Assert.That(step.Name, Is.EqualTo("build"));
        Assert.That(step.ImageResolver().Reference, Is.EqualTo("dotnet/sdk:8.0"));
        Assert.That(step.Action, Is.Not.Null);
        Assert.That(globals.Steps, Has.Count.EqualTo(1));
        Assert.That(globals.Steps[0].Name, Is.EqualTo("build"));
    }

    [Test]
    public void Step_WithoutImage_Throws()
    {
        var globals = new ScriptGlobals();
        var builder = globals.Step("build");

        Assert.Throws<InvalidOperationException>(() =>
            builder.Run(ctx => Task.CompletedTask));
    }

    [Test]
    public void Step_WithEmptyName_Throws()
    {
        var globals = new ScriptGlobals();

        Assert.Throws<ArgumentException>(() => globals.Step(""));
        Assert.Throws<ArgumentException>(() => globals.Step("   "));
        Assert.Throws<ArgumentException>(() => globals.Step<string>(""));
    }

    [Test]
    public void Step_WithEmptyImage_Throws()
    {
        var globals = new ScriptGlobals();
        var builder = globals.Step("build");

        Assert.Throws<ArgumentException>(() => builder.Image(""));
        Assert.Throws<ArgumentException>(() => builder.Image("   "));
    }

    [Test]
    public void Step_WithSyncAction_Works()
    {
        var globals = new ScriptGlobals();

        var step = globals.Step("build")
            .Image("alpine")
            .Run(ctx => { });

        Assert.That(step.Action, Is.Not.Null);
    }

    [Test]
    public async Task TypedStep_ThreadsOutputThroughCollectedAction()
    {
        var globals = new ScriptGlobals();

        var typed = globals.Step<string>("emit")
            .Image("alpine")
            .Run(ctx => Task.FromResult("hello"));

        var collected = globals.Steps.Single();
        var result = await collected.Action(null!);

        Assert.That(result, Is.EqualTo("hello"));
        Assert.That(typed.Output, Is.EqualTo("hello"), "running the collected wrapper must populate the typed step's Output");
    }
}
