using Ilmarinen.Docker;
using Ilmarinen.Scripting;
using NUnit.Framework;

namespace Ilmarinen.Core.Tests;

[TestFixture]
[Parallelizable(ParallelScope.Children)]
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
        var tempDir = Path.Combine(Path.GetTempPath(), $"ilmarinen-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Copy example to isolated workspace
            var srcPath = Path.Combine(GetExamplesDirectory(), exampleFile);
            var destPath = Path.Combine(tempDir, exampleFile);
            File.Copy(srcPath, destPath);

            var scriptResult = await PipelineScript.LoadAsync(destPath);
            var runner = new PipelineRunner(workDir: tempDir);
            var success = await runner.RunAsync(scriptResult.Steps);
            Assert.That(success, Is.True, $"Pipeline {exampleFile} failed");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
