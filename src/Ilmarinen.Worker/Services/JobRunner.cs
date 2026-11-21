using Ilmarinen.Docker;
using Ilmarinen.Models;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Ilmarinen.Scripting;
using Ilmarinen.Worker.Metrics;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private readonly BufferedHubSender _sender;
    private readonly ILogger _logger;
    private readonly LogCollector _logCollector;
    private readonly JobMetricsSampler? _metrics;
    private readonly Func<string, Task> _takeWorkspace;
    private bool _mirrorRebuilt;

    /// <summary>What the run works in, once its script has said; Unknown until then, and for good if it never gets that far.</summary>
    public WorkspaceKind WorkspaceKind { get; private set; } = WorkspaceKind.Unknown;

    public string? WorkspaceName { get; private set; }

    /// <summary>The named workspace didn't exist before this run.</summary>
    public bool WorkspaceFresh { get; private set; }

    /// <param name="takeWorkspace">Takes the persistent workspace the job is about to use: marks it active, so deletes refuse it, and reports it. Called before the job touches it; <see cref="WorkspaceManager.ReleaseActiveWorkspace"/> lets it go when the job ends.</param>
    public JobRunner(
        WorkerConfig config,
        WorkspaceManager workspaceManager,
        JobAssignment job,
        BufferedHubSender sender,
        ILogger logger,
        LogCollector logCollector,
        JobMetricsSampler? metrics,
        Func<string, Task> takeWorkspace)
    {
        _config = config;
        _workspaceManager = workspaceManager;
        _job = job;
        _sender = sender;
        _logger = logger;
        _logCollector = logCollector;
        _metrics = metrics;
        _takeWorkspace = takeWorkspace;
    }

    public async Task<JobStatus> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // New checkouts are built here and, for a persistent workspace, moved into place once complete
        var buildDir = Path.Combine(_config.WorkspacePath, _job.Id.ToString());
        PreparedJob? prepared = null;

        try
        {
            prepared = await PrepareAsync(buildDir, cancellationToken);
            if (prepared == null)
            {
                return JobStatus.Failed;
            }

            var workDir = prepared.WorkDir;

            // Verify the working directory is at the pinned commit
            var actualSha = GetHeadSha(workDir);
            if (actualSha != prepared.CommitSha)
            {
                _logger.LogError(
                    "Commit mismatch: expected {Expected} but workspace is at {Actual}",
                    prepared.CommitSha, actualSha);
                return JobStatus.Failed;
            }

            // Run pipeline with log streaming and artifact upload
            _logger.LogInformation("git describe: {Description}", prepared.Description);

            // Compute host path for Docker bind mounts (may differ when running in Docker)
            var hostWorkDir = Path.Combine(_config.HostWorkspacePath, Path.GetFileName(workDir)!);
            var artifactSaver = CreateArtifactSaver(cancellationToken);
            using var runner = new PipelineRunner(
                workDir,
                hostWorkDir,
                _config.WorkerContainerId,
                artifactSaver: artifactSaver,
                onOutput: _logCollector.AsCallback(),
                onEvent: _metrics != null ? _metrics.OnPipelineEventAsync : null);
            _metrics?.AttachPipeline(() => runner.Progress);
            var success = await runner.RunAsync(prepared.Steps, cancellationToken);

            return success ? JobStatus.Success : JobStatus.Failed;
        }
        finally
        {
            _workspaceManager.ReleaseActiveWorkspace();

            // Cleanup: only delete ephemeral workspaces
            if (prepared != null && prepared.Ephemeral)
            {
                try
                {
                    DeleteDirectory(prepared.WorkDir);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cleanup work directory");
                }
            }

            // Also cleanup the build directory if it still exists (error case before move)
            if (Directory.Exists(buildDir) && (prepared == null || prepared.WorkDir != buildDir))
            {
                try
                {
                    DeleteDirectory(buildDir);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cleanup build directory");
                }
            }
        }
    }

    private sealed record PreparedJob(string WorkDir, bool Ephemeral, string CommitSha, IReadOnlyList<Step<object?>> Steps, string Description);

    private sealed record PipelineAtCommit(string CommitSha, PipelineScriptResult? Script);

    /// <summary>
    /// Brings the mirror up to date, resolves the ref, compiles the script straight from the commit, and readies the working directory it asks for. Returns null, having logged why, when the job can't run.
    /// </summary>
    /// <remarks>The git work here can't be interrupted; <paramref name="cancellationToken"/> is checked between its parts.</remarks>
    private async Task<PreparedJob?> PrepareAsync(string buildDir, CancellationToken cancellationToken)
    {
        // 1. Fetch into the repository's mirror, pin the ref, and compile the script from that commit
        _logger.LogInformation("Fetching {RepoUrl}...", _job.RepoUrl);
        var mirrorPath = GitMirror.Sync(_config.WorkspacePath, _job.RepoUrl, _job.GitToken, _logger);
        cancellationToken.ThrowIfCancellationRequested();
        var pipeline = await ReadingMirrorAsync(() => LoadPipelineAsync(mirrorPath));
        var commitSha = pipeline.CommitSha;
        cancellationToken.ThrowIfCancellationRequested();

        // Report resolved commit to server. An unreachable server delays the report; it must not fail the job.
        await _sender.SendOrBufferAsync(nameof(IWorkerHub.ReportCommit), _job.Id, commitSha);

        if (pipeline.Script == null)
        {
            return null;
        }

        var workspaceConfig = pipeline.Script.Workspace;
        if (pipeline.Script.Steps.Count == 0)
        {
            _logger.LogError("Pipeline has no steps");
            return null;
        }

        // 2. Ready the working directory
        if (workspaceConfig == null)
        {
            WorkspaceKind = WorkspaceKind.Ephemeral;
            _logger.LogInformation("Using ephemeral workspace at {WorkDir}", buildDir);
            var description = await ReadingMirrorAsync(() => Task.FromResult(CloneFromMirror(mirrorPath, buildDir, commitSha)));
            return new PreparedJob(buildDir, true, commitSha, pipeline.Script.Steps, description);
        }

        // Persistent workspace - validate name is safe (defense-in-depth)
        var wsName = workspaceConfig.Name;
        if (!WorkspaceConfig.IsValidName(wsName, out var validationError))
        {
            _logger.LogError("Invalid workspace name rejected: {WorkspaceName} - {Error}",
                wsName, validationError);
            return null;
        }

        var workDir = Path.Combine(_config.WorkspacePath, wsName);

        // Final safety check: ensure resolved path is under workspace root
        var resolvedPath = Path.GetFullPath(workDir);
        var workspaceRoot = Path.GetFullPath(_config.WorkspacePath);
        if (!resolvedPath.StartsWith(workspaceRoot + Path.DirectorySeparatorChar) &&
            resolvedPath != workspaceRoot)
        {
            _logger.LogError(
                "Workspace path escape detected: {ResolvedPath} is not under {WorkspaceRoot}",
                resolvedPath, workspaceRoot);
            return null;
        }

        _logger.LogInformation("Using persistent workspace: {WorkspaceName}", wsName);
        await _takeWorkspace(wsName);
        WorkspaceKind = WorkspaceKind.Named;
        WorkspaceName = wsName;
        WorkspaceFresh = !Directory.Exists(workDir);

        if (Directory.Exists(workDir))
        {
            // Only the fetch reads the mirror; the reset and checkout fail on the workspace's own state, which a new mirror wouldn't change, and the fetch itself only blames the mirror once the mirror fails its own check
            _logger.LogInformation("Reusing existing workspace at {WorkDir}", workDir);
            WorkspaceGitHelper.ResetWorkspace(workDir, _job.RepoUrl);
            await ReadingMirrorAsync(() =>
            {
                WorkspaceGitHelper.FetchIntoWorkspace(workDir, mirrorPath);
                return Task.FromResult(true);
            });
            WorkspaceGitHelper.CheckoutCommit(workDir, commitSha);
            return new PreparedJob(workDir, false, commitSha, pipeline.Script.Steps, DescribeHead(workDir));
        }

        // Built aside and moved in whole, so an interrupted build never leaves a half-made workspace for the next job to reuse
        _logger.LogInformation("Creating new workspace at {WorkDir}", workDir);
        var newDescription = await ReadingMirrorAsync(() => Task.FromResult(CloneFromMirror(mirrorPath, buildDir, commitSha)));
        Directory.Move(buildDir, workDir);
        return new PreparedJob(workDir, false, commitSha, pipeline.Script.Steps, newDescription);
    }

    /// <summary>
    /// Runs <paramref name="read"/>, which reads the mirror's objects; if they turn out to be damaged, rebuilds the mirror and runs it again. One rebuild per job: damage that survives it is reported, not chased.
    /// </summary>
    private async Task<T> ReadingMirrorAsync<T>(Func<Task<T>> read)
    {
        try
        {
            return await read();
        }
        catch (Exception ex) when (!_mirrorRebuilt && GitMirror.IsDamage(ex))
        {
            _logger.LogWarning(ex, "Reading the mirror of {RepoUrl} failed; rebuilding it", _job.RepoUrl);
            _mirrorRebuilt = true;
            GitMirror.Rebuild(_config.WorkspacePath, _job.RepoUrl, _job.GitToken);
            return await read();
        }
    }

    private async Task<PipelineAtCommit> LoadPipelineAsync(string mirrorPath)
    {
        using var mirror = new Repository(mirrorPath);
        var commit = GitMirror.Resolve(mirror, _job.Ref);
        _logger.LogInformation("Resolved {Ref} to {CommitSha}", _job.Ref, commit.Sha);

        var sources = new GitTreeSourceResolver(commit);
        var scriptPath = sources.FilePathFor(_job.ScriptPath);
        if (scriptPath == null || !sources.Exists(scriptPath))
        {
            _logger.LogError("Pipeline script not found: {ScriptPath}", _job.ScriptPath);
            return new PipelineAtCommit(commit.Sha, null);
        }

        _logger.LogInformation("Loading pipeline: {ScriptPath}", _job.ScriptPath);
        return new PipelineAtCommit(commit.Sha, await PipelineScript.LoadAsync(scriptPath, sources));
    }

    /// <summary>
    /// Clones the mirror into <paramref name="path"/> at <paramref name="commitSha"/> and returns its <c>git describe</c>, which walks history the local clone copied without checking.
    /// </summary>
    private string CloneFromMirror(string mirrorPath, string path, string commitSha)
    {
        DeleteDirectory(path);
        WorkspaceGitHelper.CloneWorkspace(mirrorPath, path, _job.RepoUrl, commitSha);
        return DescribeHead(path);
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
            // IWorkerHttp.UploadArtifact: declared there so the worker protocol's shape covers it.
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

    // A symlink is deleted as itself, never followed: chmodding through one would reach files outside the tree being deleted.
    private static void SetAttributesNormal(DirectoryInfo dir)
    {
        foreach (var subDir in dir.GetDirectories().Where(d => d.LinkTarget == null))
        {
            SetAttributesNormal(subDir);
        }

        foreach (var file in dir.GetFiles().Where(f => f.LinkTarget == null))
        {
            file.Attributes = FileAttributes.Normal;
        }
    }
}
