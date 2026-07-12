using Cocona;
using Ilmarinen.Docker;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Ilmarinen.Scripting;
using System.IO;
using System.Linq;
using System.Net.Http.Json;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using System;

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
            var scriptResult = await PipelineScript.LoadAsync(script);

            if (scriptResult.Workspace != null)
            {
                Console.WriteLine($"Workspace: {scriptResult.Workspace.Name} (ignored in CLI mode)");
            }

            if (scriptResult.Steps.Count == 0)
            {
                Console.WriteLine("No steps defined in pipeline.");
                return 0;
            }

            using var runner = new PipelineRunner();
            var success = await runner.RunAsync(scriptResult.Steps);

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

    /// <summary>Check that the local environment can run pipelines (Docker, network, agent API).</summary>
    [Command("check")]
    public async Task<int> Check()
    {
        await using var diagnostic = new DockerDiagnostic(workerContainerId: null);

        // Cancel on Ctrl-C so the diagnostic's cleanup step still runs (otherwise the process gets killed mid-pull and we leak the diagnostic container/network).
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            if (!cts.IsCancellationRequested)
            {
                Console.WriteLine();
                Console.WriteLine("Cancelling — running cleanup...");
                e.Cancel = true;
                cts.Cancel();
            }
        };

        var progress = new Progress<DiagnosticStepResult>(step =>
        {
            var status = step.Success ? "[ ok ]" : "[FAIL]";
            var duration = FormatDuration(step.Duration);
            Console.WriteLine($"{status} {step.Name} ({duration})");
            if (!step.Success && step.Message != null)
                Console.WriteLine($"       {step.Message}");
            if (!step.Success && step.Suggestion != null)
                Console.WriteLine($"       Suggestion: {step.Suggestion}");
        });

        DiagnosticReport report;
        try
        {
            report = await diagnostic.RunAsync(progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.WriteLine("Cancelled.");
            return 130;
        }

        Console.WriteLine();
        if (report.Status == DiagnosticStatus.Healthy)
        {
            Console.WriteLine("Healthy: all checks passed.");
            return 0;
        }

        // Degraded means the worker can run jobs but the diagnostic couldn't tidy up after itself. That's worth
        // saying out loud, but failing the command would be wrong: nothing about the worker's capability is broken.
        if (report.Status == DiagnosticStatus.Degraded)
        {
            Console.WriteLine($"Degraded: {report.Summary}");
            return 0;
        }

        Console.WriteLine($"Unhealthy: {report.Summary}");
        return 1;
    }

    private static string FormatDuration(TimeSpan d)
    {
        if (d.TotalSeconds < 1) return $"{d.TotalMilliseconds:0}ms";
        if (d.TotalSeconds < 60) return $"{d.TotalSeconds:0.0}s";
        return $"{(int)d.TotalMinutes}m{d.Seconds:00}s";
    }

    /// <summary>Submit a job to the server</summary>
    [Command("submit")]
    public async Task<int> Submit(
        [Option('s', Description = "Server URL")] string server,
        [Option('r', Description = "Repository URL")] string repo,
        [Option("ref", Description = "Git ref")] string gitRef = "main",
        [Option(Description = "Script path")] string script = "pipeline.csx",
        [Option('t', Description = "Git token for HTTPS authentication (or set ILMARINEN_GIT_TOKEN)")] string? token = null)
    {
        // Use environment variable as fallback for token
        var gitToken = token ?? Environment.GetEnvironmentVariable("ILMARINEN_GIT_TOKEN");

        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        using var http = new HttpClient();

        // Check build compatibility with server
        try
        {
            var versionResponse = await http.GetFromJsonAsync<VersionInfo>($"{server}/api/version", jsonOptions);
            if (versionResponse?.ProtocolHash != ProtocolVersion.Hash)
            {
                Console.Error.WriteLine($"Warning: Protocol mismatch with server.");
                Console.Error.WriteLine($"  CLI:    {ProtocolVersion.Hash}");
                Console.Error.WriteLine($"  Server: {versionResponse?.ProtocolHash ?? "unknown"}");
                Console.Error.WriteLine("Rebuild CLI and server with the same protocol definitions.");
                if (Environment.GetEnvironmentVariable("DEBUG") != null)
                    Console.Error.WriteLine($"CLI protocol hash input:\n{ProtocolVersion.HashInput}");
            }
        }
        catch (HttpRequestException)
        {
            // Older server without version endpoint - proceed anyway
        }

        var submission = new JobSubmission
        {
            RepoUrl = repo,
            Ref = gitRef,
            ScriptPath = script,
            GitTokenMode = gitToken != null ? GitTokenMode.Explicit : GitTokenMode.None,
            GitToken = gitToken
        };

        var response = await http.PostAsJsonAsync($"{server}/api/jobs", submission);

        if (!response.IsSuccessStatusCode)
        {
            await WriteErrorAsync(response);
            return 1;
        }

        var result = await response.Content.ReadFromJsonAsync<JobSubmissionResult>(jsonOptions);
        var jobUrl = $"{server.TrimEnd('/')}/jobs/{result?.Id}";
        Console.WriteLine($"Job submitted: {jobUrl}");
        return 0;
    }

    /// <summary>Get job status</summary>
    [Command("status")]
    public async Task<int> Status(
        [Option('s', Description = "Server URL")] string server,
        [Argument(Description = "Job ID")] string jobId)
    {
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        using var http = new HttpClient();
        var response = await http.GetAsync($"{server}/api/jobs/{jobId}");

        if (!response.IsSuccessStatusCode)
        {
            await WriteErrorAsync(response);
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

    private static async Task WriteErrorAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var statusCode = (int)response.StatusCode;

        // Try to parse as ProblemDetails (RFC 7807)
        try
        {
            var problem = JsonSerializer.Deserialize<ProblemDetails>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (!string.IsNullOrEmpty(problem?.Detail))
            {
                Console.Error.WriteLine($"Server error ({statusCode}): {problem.Detail}");
                return;
            }
        }
        catch (JsonException)
        {
            // Not valid JSON, fall through to raw output
        }

        // Fallback: show status code and raw body
        Console.Error.WriteLine($"Server error: {statusCode} {response.ReasonPhrase}");
        if (!string.IsNullOrWhiteSpace(body))
            Console.Error.WriteLine(body);
    }
}

/// <summary>
/// RFC 7807 ProblemDetails for parsing server error responses.
/// </summary>
record ProblemDetails
{
    public int? Status { get; init; }
    public string? Title { get; init; }
    public string? Detail { get; init; }
}
