using Ilmarinen.Docker;
using Ilmarinen.Scripting;
using NUnit.Framework;

namespace Ilmarinen.Core.Tests;

[TestFixture]
public class ExamplePipelinesTests
{
    private static string GetExamplesDirectory()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(ExamplePipelinesTests).Assembly.Location)!;
        return Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", "..", "examples"));
    }

    public static IEnumerable<string> GetExampleFiles()
    {
        return Directory.GetFiles(GetExamplesDirectory(), "*.csx")
            .Select(Path.GetFileName)
            .OrderBy(f => f)!;
    }

    [Test]
    [TestCaseSource(nameof(GetExampleFiles))]
    public async Task Example_RunsSuccessfully(string exampleFile)
    {
        var path = Path.Combine(GetExamplesDirectory(), exampleFile);
        var steps = await PipelineScript.LoadAsync(path);
        var runner = new PipelineRunner();
        var success = await runner.RunAsync(steps);
        Assert.That(success, Is.True, $"Pipeline {exampleFile} failed");
    }
}
