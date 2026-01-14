using System.Net.Http.Json;
using System.Text.Json;
using Cocona;
using Ilmarinen.Docker;
using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Scripting;
using NUlid;

CoconaLiteApp.Run<Commands>(args);

public class Commands
{
    /// <summary>Run a pipeline locally (default command)</summary>
    [Command("run")]
    public async Task<int> Run(
        [Argument(Description = "Pipeline script path")] string script = "pipeline.csx")
    {
        if (!File.Exists(script))
        {
            Console.Error.WriteLine($"Pipeline script not found: {script}");
            return 1;
        }

        try
        {
            Console.WriteLine($"Loading pipeline: {script}");
            var steps = await PipelineScript.LoadAsync(script);

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

    /// <summary>Submit a job to the server</summary>
    [Command("submit")]
    public async Task<int> Submit(
        [Option('s', Description = "Server URL")] string server,
        [Option('r', Description = "Repository URL")] string repo,
        [Option("ref", Description = "Git ref")] string gitRef = "main",
        [Option(Description = "Script path")] string script = "pipeline.csx")
    {
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        jsonOptions.Converters.Add(new UlidJsonConverter());

        var submission = new JobSubmission
        {
            RepoUrl = repo,
            Ref = gitRef,
            ScriptPath = script
        };

        using var http = new HttpClient();
        var response = await http.PostAsJsonAsync($"{server}/api/jobs", submission);

        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"Error: {response.StatusCode}");
            var error = await response.Content.ReadAsStringAsync();
            Console.Error.WriteLine(error);
            return 1;
        }

        var result = await response.Content.ReadFromJsonAsync<JobSubmissionResult>(jsonOptions);
        Console.WriteLine($"Job submitted: {result?.Id}");
        return 0;
    }

    /// <summary>Get job status</summary>
    [Command("status")]
    public async Task<int> Status(
        [Option('s', Description = "Server URL")] string server,
        [Argument(Description = "Job ID")] string jobId)
    {
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        jsonOptions.Converters.Add(new UlidJsonConverter());

        using var http = new HttpClient();
        var response = await http.GetAsync($"{server}/api/jobs/{jobId}");

        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"Error: {response.StatusCode}");
            return 1;
        }

        var job = await response.Content.ReadFromJsonAsync<JobInfo>(jsonOptions);
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
}

record JobSubmissionResult
{
    public required Ulid Id { get; init; }
}
