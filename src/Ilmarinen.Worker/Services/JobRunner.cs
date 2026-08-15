using Ilmarinen.Docker;
using Ilmarinen.Models;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Ilmarinen.Scripting;
using LibGit2Sharp;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Worker.Services;

public class JobRunner
{
    private readonly WorkerConfig _config;
    private readonly WorkspaceManager _workspaceManager;
    private readonly JobAssignment _job;
    private readonly HubConnection _connection;
    private readonly ILogger _logger;
    private readonly LogCollector _logCollector;

    public JobRunner(
        WorkerConfig config,
        WorkspaceManager workspaceManager,
        JobAssignment job,
        HubConnection connection,
        ILogger logger,
        LogCollector logCollector)
    {
        _config = config;
        _workspaceManager = workspaceManager;
        _job = job;
        _connection = connection;
        _logger = logger;
        _logCollector = logCollector;
    }

    public async Task<JobResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // Start with a temporary directory to clone and read the script
        var tempDir = Path.Combine(_config.WorkspacePath, _job.Id.ToString());
        string? workDir = null;
        WorkspaceConfig? workspaceConfig = null;

        try
        {
            // 1. Clone to temp directory to read the script
            _logger.LogInformation("Cloning {RepoUrl}...", _job.RepoUrl);
            CloneRepository(tempDir);

            // 2. Checkout ref and pin the resolved commit SHA
            _logger.LogInformation("Checking out {Ref}...", _job.Ref);
            var commitSha = CheckoutRef(tempDir);
            _logger.LogInformation("Resolved {Ref} to {CommitSha}", _job.Ref, commitSha);

            // Report resolved commit to server
            await _connection.InvokeCoreAsync("ReportCommit", [_job.Id, commitSha]);

            // 3. Load pipeline script to get workspace config
            var scriptPath = Path.Combine(tempDir, _job.ScriptPath);
            if (!File.Exists(scriptPath))
            {
                _logger.LogError("Pipeline script not found: {ScriptPath}", _job.ScriptPath);
                return new JobResult { Id = _job.Id, Status = JobStatus.Failed };
            }

            _logger.LogInformation("Loading pipeline: {ScriptPath}", _job.ScriptPath);
            var scriptResult = await PipelineScript.LoadAsync(scriptPath);
            workspaceConfig = scriptResult.Workspace;

            if (scriptResult.Steps.Count == 0)
            {
                _logger.LogError("Pipeline has no steps");
                return new JobResult { Id = _job.Id, Status = JobStatus.Failed };
            }

            // 4. Determine workspace path and prepare it
            if (workspaceConfig != null)
            {
                // Persistent workspace - validate name is safe (defense-in-depth)
                var wsName = workspaceConfig.Name;
                if (!WorkspaceConfig.IsValidName(wsName, out var validationError))
                {
                    _logger.LogError("Invalid workspace name rejected: {WorkspaceName} - {Error}",
                        wsName, validationError);
                    return new JobResult { Id = _job.Id, Status = JobStatus.Failed };
                }

                workDir = Path.Combine(_config.WorkspacePath, wsName);

                // Final safety check: ensure resolved path is under workspace root
                var resolvedPath = Path.GetFullPath(workDir);
                var workspaceRoot = Path.GetFullPath(_config.WorkspacePath);
                if (!resolvedPath.StartsWith(workspaceRoot + Path.DirectorySeparatorChar) &&
                    resolvedPath != workspaceRoot)
                {
                    _logger.LogError(
                        "Workspace path escape detected: {ResolvedPath} is not under {WorkspaceRoot}",
                        resolvedPath, workspaceRoot);
                    return new JobResult { Id = _job.Id, Status = JobStatus.Failed };
                }

                _logger.LogInformation("Using persistent workspace: {WorkspaceName}", wsName);
                _workspaceManager.SetActiveWorkspace(wsName);

                if (Directory.Exists(workDir))
                {
                    // Workspace exists - delete temp, use existing workspace
                    _logger.LogInformation("Reusing existing workspace at {WorkDir}", workDir);
                    DeleteDirectory(tempDir);

                    // Validate and update existing workspace, using pinned commit SHA
                    WorkspaceGitHelper.PrepareWorkspace(workDir, _job.RepoUrl, commitSha, _job.GitToken);
                }
                else
                {
                    // Workspace doesn't exist - move temp to workspace path
                    _logger.LogInformation("Creating new workspace at {WorkDir}", workDir);
                    Directory.Move(tempDir, workDir);
                }
            }
            else
            {
                // Ephemeral workspace - use temp directory
                workDir = tempDir;
                _logger.LogInformation("Using ephemeral workspace at {WorkDir}", workDir);
            }

            // 5. Verify the working directory is at the pinned commit
            var actualSha = GetHeadSha(workDir);
            if (actualSha != commitSha)
            {
                _logger.LogError(
                    "Commit mismatch: expected {Expected} but workspace is at {Actual}",
                    commitSha, actualSha);
                return new JobResult { Id = _job.Id, Status = JobStatus.Failed };
            }

            // 6. Run pipeline with log streaming and artifact upload
            _logger.LogInformation("git describe: {Description}", DescribeHead(workDir));

            // Compute host path for Docker bind mounts (may differ when running in Docker)
            var hostWorkDir = Path.Combine(_config.HostWorkspacePath, Path.GetFileName(workDir)!);
            var artifactSaver = CreateArtifactSaver(cancellationToken);
            using var runner = new PipelineRunner(
                workDir,
                hostWorkDir,
                _config.WorkerContainerId,
                artifactSaver: artifactSaver,
                onOutput: _logCollector.AsCallback());
            var success = await runner.RunAsync(scriptResult.Steps, cancellationToken);

            return new JobResult
            {
                Id = _job.Id,
                Status = success ? JobStatus.Success : JobStatus.Failed
            };
        }
        finally
        {
            _workspaceManager.SetActiveWorkspace(null);

            // Cleanup: only delete ephemeral workspaces
            if (workspaceConfig == null && workDir != null)
            {
                try
                {
                    DeleteDirectory(workDir);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cleanup work directory");
                }
            }

            // Also cleanup temp dir if it still exists (error case before move)
            if (workDir != tempDir && Directory.Exists(tempDir))
            {
                try
                {
                    DeleteDirectory(tempDir);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cleanup temp directory");
                }
            }
        }
    }

    private void CloneRepository(string workDir)
    {
        var options = new CloneOptions();

        if (!string.IsNullOrEmpty(_job.GitToken))
        {
            options.FetchOptions.CredentialsProvider = (url, user, types) =>
                new UsernamePasswordCredentials
                {
                    Username = "git",
                    Password = _job.GitToken
                };
        }

        Repository.Clone(_job.RepoUrl, workDir, options);
    }

    private string CheckoutRef(string workDir)
    {
        return WorkspaceGitHelper.CheckoutRef(workDir, _job.Ref);
    }

    private static string GetHeadSha(string workDir)
    {
        using var repo = new Repository(workDir);
        return repo.Head.Tip.Sha;
    }

    private static string DescribeHead(string workDir)
    {
        using var repo = new Repository(workDir);
        return repo.Describe(repo.Head.Tip, new DescribeOptions
        {
            UseCommitIdAsFallback = true,
        });
    }

    private void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            SetAttributesNormal(new DirectoryInfo(path));
            Directory.Delete(path, recursive: true);
        }
    }

    // Shared across jobs; the worker only ever talks to its one configured server. The generous timeout covers large artifact bodies.
    private static readonly HttpClient ArtifactHttpClient = new() { Timeout = TimeSpan.FromMinutes(30) };

    private Func<string, string?, Task<ArtifactRef>> CreateArtifactSaver(CancellationToken jobToken)
    {
        var baseUrl = _config.ServerUrl;

        return async (localPath, name) =>
        {
            var artifactName = name ?? Path.GetFileName(localPath);
            var fileInfo = new FileInfo(localPath);
            var url = $"{baseUrl}/hub/workers/jobs/{_job.Id}/artifacts?name={Uri.EscapeDataString(artifactName)}";

            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            };

            _logger.LogInformation("Uploading artifact {Name} ({Size} bytes)...", artifactName, fileInfo.Length);

            const int maxAttempts = 5;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await using var fileStream = File.OpenRead(localPath);
                    var content = new StreamContent(fileStream);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    content.Headers.ContentLength = fileInfo.Length;

                    var response = await ArtifactHttpClient.PostAsync(url, content, jobToken);
                    response.EnsureSuccessStatusCode();

                    var result = await response.Content.ReadFromJsonAsync<ArtifactInfo>(jsonOptions, jobToken);

                    _logger.LogInformation("Artifact uploaded: {Id} ({Name})", result!.Id, result.Name);

                    return new ArtifactRef
                    {
                        Name = result.Name,
                        Size = result.Size
                    };
                }
                catch (HttpRequestException ex) when (attempt < maxAttempts && (ex.StatusCode == null || (int)ex.StatusCode >= 500))
                {
                    _logger.LogWarning(ex, "Artifact upload failed (attempt {Attempt}/{MaxAttempts}), retrying in 5 seconds...", attempt, maxAttempts);
                    await Task.Delay(5000, jobToken);
                }
            }
        };
    }

    private static void SetAttributesNormal(DirectoryInfo dir)
    {
        foreach (var subDir in dir.GetDirectories())
        {
            SetAttributesNormal(subDir);
        }

        foreach (var file in dir.GetFiles())
        {
            file.Attributes = FileAttributes.Normal;
        }
    }
}
