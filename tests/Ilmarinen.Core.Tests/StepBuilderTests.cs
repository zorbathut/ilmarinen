using Ilmarinen.Models;
using Xunit;

namespace Ilmarinen.Core.Tests;

public class StepBuilderTests
{
    [Fact]
    public void BasicStep_CreatesStep()
    {
        var step = new StepBuilder("build")
            .Image("dotnet/sdk:8.0")
            .Run(ctx => Task.CompletedTask);

        Assert.Equal("build", step.Name);
        Assert.Equal("dotnet/sdk:8.0", step.ImageResolver().Reference);
        Assert.NotNull(step.Action);
    }

    [Fact]
    public void Step_WithoutImage_Throws()
    {
        var builder = new StepBuilder("build");

        Assert.Throws<InvalidOperationException>(() =>
            builder.Run(ctx => Task.CompletedTask));
    }

    [Fact]
    public void StepBuilder_WithEmptyName_Throws()
    {
        Assert.Throws<ArgumentException>(() => new StepBuilder(""));
        Assert.Throws<ArgumentException>(() => new StepBuilder("   "));
    }

    [Fact]
    public void StepBuilder_WithEmptyImage_Throws()
    {
        var builder = new StepBuilder("build");
        Assert.Throws<ArgumentException>(() => builder.Image(""));
        Assert.Throws<ArgumentException>(() => builder.Image("   "));
    }

    [Fact]
    public void Step_WithSyncAction_Works()
    {
        var executed = false;
        var step = new StepBuilder("build")
            .Image("alpine")
            .Run(ctx => { executed = true; });

        Assert.NotNull(step.Action);
    }
}
