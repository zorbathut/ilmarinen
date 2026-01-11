using Ilmarinen.Docker;
using Ilmarinen.Scripting;
using Xunit;

namespace Ilmarinen.Core.Tests;

public class ExamplePipelinesTests
{
    private static string GetExamplesDirectory()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(ExamplePipelinesTests).Assembly.Location)!;
        return Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", "..", "examples"));
    }

    public static TheoryData<string> GetExampleFiles()
    {
        var examplesDir = GetExamplesDirectory();
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(examplesDir, "*.csx").OrderBy(f => f))
            data.Add(Path.GetFileName(file));
        return data;
    }

    [Theory]
    [MemberData(nameof(GetExampleFiles))]
    public async Task Example_RunsSuccessfully(string exampleFile)
    {
        var path = Path.Combine(GetExamplesDirectory(), exampleFile);
        var steps = await PipelineScript.LoadAsync(path);
        var runner = new PipelineRunner();
        var success = await runner.RunAsync(steps);
        Assert.True(success, $"Pipeline {exampleFile} failed");
    }
}
