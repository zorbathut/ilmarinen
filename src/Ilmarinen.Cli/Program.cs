using System.Net.Http.Json;
using Ilmarinen.Docker;
using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Scripting;
using NUlid;

var command = args.Length > 0 ? args[0] : "run";

return command switch
{
    "run" => await RunLocalAsync(args.Skip(1).ToArray()),
    "submit" => await SubmitJobAsync(args.Skip(1).ToArray()),
    "status" => await GetStatusAsync(args.Skip(1).ToArray()),
    "--help" or "-h" or "help" => ShowHelp(),
    _ when !command.StartsWith("-") && File.Exists(command) => await RunLocalAsync(args),
    _ => ShowHelp()
};

int ShowHelp()
{
    Console.WriteLine("ilmarinen - Container-native CI/CD");
    Console.WriteLine();
    Console.WriteLine("Usage: ilmarinen <command> [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  run [script.csx]                      Run a pipeline locally (default)");
    Console.WriteLine("  submit --server <url> --repo <url>    Submit a job to the server");
    Console.WriteLine("  status --server <url> <job-id>        Get job status");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  ilmarinen run pipeline.csx");
    Console.WriteLine("  ilmarinen submit --server http://localhost:5000 --repo https://github.com/org/repo --ref main");
    Console.WriteLine("  ilmarinen status --server http://localhost:5000 01ABC123...");
    return 0;
}

async Task<int> RunLocalAsync(string[] args)
{
    var scriptPath = args.Length > 0 ? args[0] : "pipeline.csx";

    if (!File.Exists(scriptPath))
    {
        Console.Error.WriteLine($"Pipeline script not found: {scriptPath}");
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
            Console.Error.WriteLine(ex.StackTrace);
        return 1;
    }
}

async Task<int> SubmitJobAsync(string[] args)
{
    var serverUrl = GetArg(args, "--server");
    var repoUrl = GetArg(args, "--repo");
    var gitRef = GetArg(args, "--ref") ?? "main";
    var script = GetArg(args, "--script") ?? "pipeline.csx";

    if (string.IsNullOrEmpty(serverUrl))
    {
        Console.Error.WriteLine("Error: --server is required");
        Console.Error.WriteLine("Usage: ilmarinen submit --server <url> --repo <url> [--ref main] [--script pipeline.csx]");
        return 1;
    }

    if (string.IsNullOrEmpty(repoUrl))
    {
        Console.Error.WriteLine("Error: --repo is required");
        Console.Error.WriteLine("Usage: ilmarinen submit --server <url> --repo <url> [--ref main] [--script pipeline.csx]");
        return 1;
    }

    var submission = new JobSubmission
    {
        RepoUrl = repoUrl,
        Ref = gitRef,
        ScriptPath = script
    };

    using var http = new HttpClient();
    var response = await http.PostAsJsonAsync($"{serverUrl}/api/jobs", submission);

    if (!response.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"Error: {response.StatusCode}");
        var error = await response.Content.ReadAsStringAsync();
        Console.Error.WriteLine(error);
        return 1;
    }

    var result = await response.Content.ReadFromJsonAsync<JobSubmissionResult>();
    Console.WriteLine($"Job submitted: {result?.Id}");
    return 0;
}

async Task<int> GetStatusAsync(string[] args)
{
    var serverUrl = GetArg(args, "--server");
    var jobId = args.FirstOrDefault(a => !a.StartsWith("-"));

    if (string.IsNullOrEmpty(serverUrl))
    {
        Console.Error.WriteLine("Error: --server is required");
        Console.Error.WriteLine("Usage: ilmarinen status --server <url> <job-id>");
        return 1;
    }

    if (string.IsNullOrEmpty(jobId))
    {
        Console.Error.WriteLine("Error: job ID required");
        Console.Error.WriteLine("Usage: ilmarinen status --server <url> <job-id>");
        return 1;
    }

    using var http = new HttpClient();
    var response = await http.GetAsync($"{serverUrl}/api/jobs/{jobId}");

    if (!response.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"Error: {response.StatusCode}");
        return 1;
    }

    var job = await response.Content.ReadFromJsonAsync<JobInfo>();
    if (job == null)
    {
        Console.Error.WriteLine("Job not found");
        return 1;
    }

    Console.WriteLine($"ID:         {job.Id}");
    Console.WriteLine($"Status:     {job.Status}");
    Console.WriteLine($"Repository: {job.RepoUrl}");
    Console.WriteLine($"Ref:        {job.Ref}");
    Console.WriteLine($"Worker:     {job.WorkerId?.ToString() ?? "(none)"}");
    Console.WriteLine($"Created:    {job.CreatedAt:u}");
    if (job.StartedAt.HasValue)
        Console.WriteLine($"Started:    {job.StartedAt:u}");
    if (job.CompletedAt.HasValue)
        Console.WriteLine($"Completed:  {job.CompletedAt:u}");

    return job.Status == JobStatus.Success ? 0 : 1;
}

static string? GetArg(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name)
            return args[i + 1];
    }
    return null;
}

record JobSubmissionResult
{
    public required Ulid Id { get; init; }
}
