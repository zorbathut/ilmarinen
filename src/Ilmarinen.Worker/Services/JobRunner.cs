using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ilmarinen.Docker;
using Ilmarinen.Models;
using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Scripting;
using LibGit2Sharp;
using Microsoft.AspNetCore.SignalR.Client;
using NUlid;

namespace Ilmarinen.Worker.Services;

public class JobRunner
{
    private readonly WorkerConfig _config;
    private readonly JobAssignment _job;
    private readonly HubConnection _connection;
    private readonly ILogger _logger;
    private readonly LogCollector _logCollector;

    public JobRunner(
        WorkerConfig config,
        JobAssignment job,
        HubConnection connection,
        ILogger logger,
        LogCollector logCollector)
    {
        _config = config;
        _job = job;
        _connection = connection;
        _logger = logger;
        _logCollector = logCollector;
    }

    public async Task<JobCompleted> ExecuteAsync()
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

            // 2. Checkout ref
            _logger.LogInformation("Checking out {Ref}...", _job.Ref);
            CheckoutRef(tempDir);

            // 3. Load pipeline script to get workspace config
            var scriptPath = Path.Combine(tempDir, _job.ScriptPath);
            if (!File.Exists(scriptPath))
            {
                _logger.LogError("Pipeline script not found: {ScriptPath}", _job.ScriptPath);
                return new JobCompleted { Id = _job.Id, Status = JobStatus.Failed };
            }

            _logger.LogInformation("Loading pipeline: {ScriptPath}", _job.ScriptPath);
            var scriptResult = await PipelineScript.LoadAsync(scriptPath);
            workspaceConfig = scriptResult.Workspace;

            if (scriptResult.Steps.Count == 0)
            {
                _logger.LogError("Pipeline has no steps");
                return new JobCompleted { Id = _job.Id, Status = JobStatus.Failed };
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
                    return new JobCompleted { Id = _job.Id, Status = JobStatus.Failed };
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
                    return new JobCompleted { Id = _job.Id, Status = JobStatus.Failed };
                }

                _logger.LogInformation("Using persistent workspace: {WorkspaceName}", wsName);

                if (Directory.Exists(workDir))
                {
                    // Workspace exists - delete temp, use existing workspace
                    _logger.LogInformation("Reusing existing workspace at {WorkDir}", workDir);
                    DeleteDirectory(tempDir);

                    // Validate and update existing workspace
                    WorkspaceGitHelper.PrepareWorkspace(workDir, _job.RepoUrl, _job.Ref, _job.GitToken);
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

            // 5. Run pipeline with log streaming and artifact upload
            _logger.LogInformation("Running {StepCount} step(s)...", scriptResult.Steps.Count);

            // Compute host path for Docker bind mounts (may differ when running in Docker)
            var hostWorkDir = Path.Combine(_config.GetHostWorkspacePath(), Path.GetFileName(workDir)!);
            var artifactSaver = CreateArtifactSaver();
            var runner = new PipelineRunner(workDir, hostWorkDir, artifactSaver: artifactSaver, onOutput: _logCollector.AsCallback());
            var success = await runner.RunAsync(scriptResult.Steps);

            return new JobCompleted
            {
                Id = _job.Id,
                Status = success ? JobStatus.Success : JobStatus.Failed
            };
        }
        finally
        {
            // Flush any remaining logs
            try
            {
                await _logCollector.FlushAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to flush logs");
            }

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

    private void CheckoutRef(string workDir)
    {
        using var repo = new Repository(workDir);

        var target = repo.Lookup(_job.Ref);
        if (target != null)
        {
            Commands.Checkout(repo, target as Commit ?? ((GitObject)target).Peel<Commit>());
        }
        else
        {
            var remoteBranch = repo.Branches[$"origin/{_job.Ref}"];
            if (remoteBranch != null)
            {
                Commands.Checkout(repo, remoteBranch.Tip);
            }
        }
    }

    private void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            SetAttributesNormal(new DirectoryInfo(path));
            Directory.Delete(path, recursive: true);
        }
    }

    private Func<string, string?, Task<ArtifactRef>> CreateArtifactSaver()
    {
        var httpClient = new HttpClient();
        var baseUrl = _config.GetPublicApiUrl();

        return async (hostPath, name) =>
        {
            var artifactName = name ?? Path.GetFileName(hostPath);

            await using var fileStream = File.OpenRead(hostPath);
            var fileInfo = new FileInfo(hostPath);

            _logger.LogInformation("Uploading artifact {Name} ({Size} bytes)...", artifactName, fileInfo.Length);

            var content = new StreamContent(fileStream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = fileInfo.Length;

            var url = $"{baseUrl}/api/jobs/{_job.Id}/artifacts?name={Uri.EscapeDataString(artifactName)}";

            var response = await httpClient.PostAsync(url, content);
            response.EnsureSuccessStatusCode();

            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new UlidJsonConverter() }
            };
            var result = await response.Content.ReadFromJsonAsync<ArtifactUploadResponse>(jsonOptions);

            _logger.LogInformation("Artifact uploaded: {Id} ({Name})", result!.Id, result.Name);

            return new ArtifactRef
            {
                Id = result.Id.ToString(),
                Name = result.Name,
                Size = result.Size
            };
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

    private record ArtifactUploadResponse(Ulid Id, string Name, long Size);
}
