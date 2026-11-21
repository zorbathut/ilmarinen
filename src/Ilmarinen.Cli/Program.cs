using Cocona.Application;
using Cocona;
using Ilmarinen.Cli;
using Ilmarinen.Docker;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Ilmarinen.Scripting;
using System.IO;
using System.Linq;
using System.Net.Http.Json;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using System;

CoconaLiteApp.Run<Commands>(args);

internal class Commands
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

            // Cancel on Ctrl-C so the run tears its step container and network down (otherwise the process dies mid-step and leaves both behind).
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

            using var runner = new PipelineRunner();
            var success = await runner.RunAsync(scriptResult.Steps, cts.Token);

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
        // No extra steps: this checks the local Docker environment, not a worker. Anything a worker adds on top (host sleep inhibition, say) depends on how the worker is deployed and would answer differently here anyway.
        await using var diagnostic = new DockerDiagnostic(workerContainerId: null, extraSteps: []);

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
        [Option('t', Description = "Git token for HTTPS authentication (or set ILMARINEN_GIT_TOKEN)")] string? token = null,
        [Option("min-worker-priority", Description = "Only run on workers of at least this worker priority, by name; \"any\" for no minimum")] string? minWorkerPriority = null,
        [Option("priority", Description = "Job priority, by name: higher-priority queued jobs are dispatched first")] string? priority = null)
    {
        // Use environment variable as fallback for token
        var gitToken = token ?? Environment.GetEnvironmentVariable("ILMARINEN_GIT_TOKEN");

        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        using var http = new HttpClient();
        await CheckProtocolAsync(http, server, jsonOptions);

        var submission = new JobSubmission
        {
            RepoUrl = repo,
            Ref = gitRef,
            ScriptPath = script,
            GitTokenMode = gitToken != null ? GitTokenMode.Explicit : GitTokenMode.None,
            GitToken = gitToken,
            MinWorkerPriority = minWorkerPriority,
            Priority = priority
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

    private const string FollowExitCodes = "exit 0 if the job succeeded, 3 if it failed, 4 if it was cancelled, 2 if it could not be followed, 1 if it could not be started, 130 if interrupted";

    /// <summary>Trigger a registered pipeline</summary>
    [Command("trigger")]
    public async Task<int> Trigger(
        [FromService] ICoconaAppContextAccessor contextAccessor,
        [Option('s', Description = "Server URL")] string server,
        [Argument(Description = "Pipeline ID")] Guid pipelineId,
        [Option("ref", Description = "Git ref (defaults to the pipeline's own)")] string? gitRef = null,
        [Option("min-worker-priority", Description = "Only run on workers of at least this worker priority, by name; \"any\" for no minimum")] string? minWorkerPriority = null,
        [Option("priority", Description = "Job priority, by name: higher-priority queued jobs are dispatched first")] string? priority = null,
        [Option('f', Description = "Stream the job's log until it finishes; " + FollowExitCodes + ". Interrupting cancels the job")] bool follow = false)
    {
        var serverUrl = NormalizeServerUrl(server);
        if (serverUrl == null)
        {
            return 1;
        }

        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        using var http = new HttpClient();
        await CheckProtocolAsync(http, serverUrl, jsonOptions);

        var trigger = new PipelineTrigger
        {
            Ref = gitRef,
            MinWorkerPriority = minWorkerPriority,
            Priority = priority
        };

        var response = await http.PostAsJsonAsync($"{serverUrl}/api/pipelines/{pipelineId}/trigger", trigger);

        if (!response.IsSuccessStatusCode)
        {
            await WriteErrorAsync(response);
            return 1;
        }

        var result = await response.Content.ReadFromJsonAsync<JobSubmissionResult>(jsonOptions);
        var jobId = result!.Id;

        // While following, stdout carries the job's own stdout and nothing else.
        var announce = follow ? Console.Error : Console.Out;
        announce.WriteLine($"Job submitted: {serverUrl}/jobs/{jobId}");

        if (!follow)
        {
            return 0;
        }

        return await FollowJobAsync(serverUrl, jobId, cancelOnInterrupt: true, contextAccessor.Current!.CancellationToken);
    }

    /// <summary>Stream a job's log until it finishes; exit 0 if the job succeeded, 3 if it failed, 4 if it was cancelled, 2 if it could not be followed, 130 if interrupted. Interrupting stops following but leaves the job running</summary>
    [Command("follow")]
    public async Task<int> Follow(
        [FromService] ICoconaAppContextAccessor contextAccessor,
        [Option('s', Description = "Server URL")] string server,
        [Argument(Description = "Job ID")] Guid jobId)
    {
        var serverUrl = NormalizeServerUrl(server);
        if (serverUrl == null)
        {
            return 1;
        }

        return await FollowJobAsync(serverUrl, jobId, cancelOnInterrupt: false, contextAccessor.Current!.CancellationToken);
    }

    /// <summary>Idle time between feed reads while following. Output that arrives in bulk is read back to back; this only bounds how late a quiet job's next line or its completion is noticed.</summary>
    private static readonly TimeSpan FollowPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Long enough to ride out a server redeploy, short enough that a reader blocked on a dead server finds out.</summary>
    private static readonly TimeSpan FollowGiveUpAfter = TimeSpan.FromMinutes(2);

    /// <summary>Cocona ends the process a few seconds after an interrupt whether or not the command has finished, so cancelling the job has to fit inside that.</summary>
    private static readonly TimeSpan FollowCancelTimeout = TimeSpan.FromSeconds(3);

    /// <param name="interrupted">Cocona's own token, cancelled on Ctrl-C and on SIGTERM — the latter being what a supervising tool sends when it gives up waiting.</param>
    private static async Task<int> FollowJobAsync(string serverUrl, Guid jobId, bool cancelOnInterrupt, CancellationToken interrupted)
    {
        var logUrl = $"{serverUrl}/api/jobs/{jobId}/logs/raw";

        using var http = new HttpClient { BaseAddress = new Uri(serverUrl + "/") };

        // Worker meta lines share stderr with the job's own, so a terminal shows them dimmed; a redirected stream gets the text untouched.
        var dimMeta = !Console.IsErrorRedirected;

        var follower = new JobLogFollower(
            http,
            jobId,
            FollowPollInterval,
            FollowGiveUpAfter,
            entry => WriteLogEntry(entry, dimMeta),
            status =>
            {
                // The final status gets its own summary line below.
                if (status is not (JobStatus.Success or JobStatus.Failed or JobStatus.Cancelled))
                {
                    Console.Error.WriteLine($"Job {jobId}: {status}");
                }
            },
            warning => Console.Error.WriteLine($"Warning: {warning}"));

        try
        {
            var status = await follower.FollowAsync(interrupted);
            Console.Error.WriteLine($"Job {jobId} finished: {status} — full log: {logUrl}");

            return status switch
            {
                JobStatus.Success => 0,
                JobStatus.Failed => 3,
                JobStatus.Cancelled => 4,
                _ => throw new InvalidOperationException($"Follow ended on non-terminal status {status}")
            };
        }
        catch (OperationCanceledException) when (interrupted.IsCancellationRequested)
        {
            Console.Error.WriteLine();

            if (!cancelOnInterrupt)
            {
                Console.Error.WriteLine($"Stopped following job {jobId}; the job itself was not cancelled. Full log: {logUrl}");
                return 130;
            }

            // The job was started for this follow, so it shouldn't outlive it. Its teardown output arrives after the cancel has already made it terminal, so there is nothing left worth waiting for.
            await CancelJobAsync(http, jobId, logUrl);
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error following job {jobId}: {ex.Message}");
            if (Environment.GetEnvironmentVariable("DEBUG") != null)
            {
                Console.Error.WriteLine(ex.StackTrace);
            }

            return 2;
        }
    }

    private static async Task CancelJobAsync(HttpClient http, Guid jobId, string logUrl)
    {
        using var timeout = new CancellationTokenSource(FollowCancelTimeout);

        try
        {
            using var response = await http.DeleteAsync($"api/jobs/{jobId}", timeout.Token);

            if (response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"Cancelled job {jobId}. Full log: {logUrl}");
            }
            else if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // The server answers a cancel of a job that already finished the same way as one of a job that doesn't exist, and this job did exist.
                Console.Error.WriteLine($"Job {jobId} had already finished, so there was nothing to cancel. Full log: {logUrl}");
            }
            else
            {
                Console.Error.Write($"Failed to cancel job {jobId}: ");
                await WriteErrorAsync(response);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException || timeout.IsCancellationRequested)
        {
            Console.Error.WriteLine($"Failed to cancel job {jobId}, which may still be running: {ex.Message}");
        }
    }

    private static void WriteLogEntry(LogEntry entry, bool dimMeta)
    {
        switch (entry.Type)
        {
            case "o":
                Console.Out.Write(entry.Data);
                break;
            case "e":
                Console.Error.Write(entry.Data);
                break;
            default:
                Console.Error.Write(dimMeta ? $"\u001b[2m{entry.Data}\u001b[0m" : entry.Data);
                break;
        }
    }

    /// <summary>The server URL without a trailing slash, or null — after saying why — when it isn't an absolute http(s) URL. A bare "host:port" parses as a URL whose scheme is the host, so the scheme has to be checked too.</summary>
    private static string? NormalizeServerUrl(string server)
    {
        if (Uri.TryCreate(server, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            return server.TrimEnd('/');
        }

        Console.Error.WriteLine($"Server URL must be an absolute http:// or https:// URL, not '{server}'.");
        return null;
    }

    /// <summary>Warns, without failing the command, when the server's protocol differs from this CLI's or can't be checked.</summary>
    private static async Task CheckProtocolAsync(HttpClient http, string server, JsonSerializerOptions jsonOptions)
    {
        VersionInfo? versionResponse;
        try
        {
            versionResponse = await http.GetFromJsonAsync<VersionInfo>($"{server}/api/version", jsonOptions);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            Console.Error.WriteLine($"Warning: could not check protocol compatibility with the server: {ex.Message}");
            return;
        }

        if (versionResponse?.ProtocolHash != ProtocolVersion.Hash)
        {
            Console.Error.WriteLine($"Warning: Protocol mismatch with server.");
            Console.Error.WriteLine($"  CLI:    {ProtocolVersion.Hash}");
            Console.Error.WriteLine($"  Server: {versionResponse?.ProtocolHash ?? "unknown"}");
            Console.Error.WriteLine("Rebuild CLI and server with the same protocol definitions.");
            if (Environment.GetEnvironmentVariable("DEBUG") != null)
            {
                Console.Error.WriteLine($"CLI protocol hash input:\n{ProtocolVersion.HashInput}");
            }
        }
    }

    /// <summary>How often --wait asks what is still running. A drain waits on jobs that run for minutes; nothing gains from asking faster.</summary>
    private static readonly TimeSpan DrainPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>Hold dispatch for the whole fleet before a deploy, so no new job starts and what is running can finish; the server's restart ends the hold. Exit 0 once held (with --wait, once nothing is running), 1 on error, 130 if interrupted while waiting (the drain stays on)</summary>
    [Command("drain")]
    public async Task<int> Drain(
        [FromService] ICoconaAppContextAccessor contextAccessor,
        [Option('s', Description = "Server URL")] string server,
        [Option("wait", Description = "Wait until no job is running")] bool wait = false,
        [Option("off", Description = "Release the drain instead, dispatching again")] bool off = false)
    {
        var serverUrl = NormalizeServerUrl(server);
        if (serverUrl == null)
        {
            return 1;
        }
        if (off && wait)
        {
            Console.Error.WriteLine("--wait waits for a drain to empty; there is nothing to wait for with --off.");
            return 1;
        }

        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var interrupted = contextAccessor.Current!.CancellationToken;
        using var http = new HttpClient();
        try
        {
            var response = await http.PutAsJsonAsync($"{serverUrl}/api/drain", new DrainUpdate { Draining = !off }, interrupted);
            if (!response.IsSuccessStatusCode)
            {
                await WriteErrorAsync(response);
                return 1;
            }
            var status = (await response.Content.ReadFromJsonAsync<DrainStatus>(jsonOptions, interrupted))!;

            if (off)
            {
                Console.Error.WriteLine("Drain released: dispatching again.");
                return 0;
            }

            Console.Error.WriteLine("Draining: no new jobs start until the drain is released or the server restarts.");
            string? last = null;
            while (status.Running.Count > 0)
            {
                // Which jobs, and on which workers, so a job left Running by a worker that has gone away shows up as the thing holding the wait.
                var running = string.Join(", ", status.Running.Select(j => $"{j.JobId} on {j.WorkerName ?? "a revoked worker"}"));
                if (running != last)
                {
                    Console.Error.WriteLine($"{(wait ? "Waiting on" : "Still")} {status.Running.Count} running: {running}");
                    last = running;
                }
                if (!wait)
                {
                    return 0;
                }

                await Task.Delay(DrainPollInterval, interrupted);
                status = (await http.GetFromJsonAsync<DrainStatus>($"{serverUrl}/api/drain", jsonOptions, interrupted))!;
                if (!status.Draining)
                {
                    Console.Error.WriteLine("The drain was released (or the server restarted) while waiting.");
                    return 1;
                }
            }

            Console.Error.WriteLine("Nothing running: ready to deploy.");
            return 0;
        }
        catch (OperationCanceledException) when (interrupted.IsCancellationRequested)
        {
            Console.Error.WriteLine(off ? "Interrupted." : "Stopped; the drain is still on unless the server never got the request.");
            return 130;
        }
        catch (HttpRequestException ex)
        {
            // A server restarting mid-wait (someone deployed already) ends the drain too, so the wait can't be resumed as it was.
            Console.Error.WriteLine($"Error talking to the server: {ex.Message}");
            return 1;
        }
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
        Console.WriteLine($"Min worker: {job.MinWorkerPriority?.Name ?? "any"} priority");
        Console.WriteLine($"Priority:   {job.Priority.Name} (job)");
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
