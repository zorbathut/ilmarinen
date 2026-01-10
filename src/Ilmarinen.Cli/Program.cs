using Ilmarinen.Docker;
using Ilmarinen.Scripting;

var scriptPath = args.Length > 0 ? args[0] : "pipeline.csx";

if (!File.Exists(scriptPath))
{
    Console.Error.WriteLine($"Pipeline script not found: {scriptPath}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Usage: ilmarinen [pipeline.csx]");
    return 1;
}

try
{
    Console.WriteLine($"Loading pipeline: {scriptPath}");
    var steps = await PipelineScript.LoadAsync(scriptPath);

    if (steps.Count == 0)
    {
        Console.WriteLine("No steps defined in pipeline.");
        return 0;
    }

    var runner = new PipelineRunner();
    var success = await runner.RunAsync(steps);

    return success ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    if (Environment.GetEnvironmentVariable("DEBUG") != null)
    {
        Console.Error.WriteLine(ex.StackTrace);
    }
    return 1;
}
