using Ilmarinen.Models;
using Ilmarinen.Protocol.Requests;
using NUlid;

namespace Ilmarinen.Worker.Services;

public class WorkspaceManager
{
    private readonly WorkerConfig _config;
    private readonly ILogger<WorkspaceManager> _logger;
    private string? _activeWorkspace;
    private readonly object _lock = new();

    public WorkspaceManager(WorkerConfig config, ILogger<WorkspaceManager> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Lists persistent workspace directories (filters out ULID-named ephemeral dirs).
    /// </summary>
    public List<string> DiscoverWorkspaces()
    {
        var workspacePath = _config.WorkspacePath;
        if (!Directory.Exists(workspacePath))
            return [];

        var result = new List<string>();
        foreach (var dir in Directory.GetDirectories(workspacePath))
        {
            var name = Path.GetFileName(dir);
            // Skip ULID-named directories (ephemeral workspaces)
            if (Ulid.TryParse(name, out _))
                continue;

            result.Add(name);
        }

        return result;
    }

    /// <summary>
    /// Tracks which workspace the current job is using.
    /// </summary>
    public void SetActiveWorkspace(string? name)
    {
        lock (_lock)
        {
            _activeWorkspace = name;
        }
    }

    /// <summary>
    /// Attempts to delete a workspace directory. Refuses if in use by a running job.
    /// </summary>
    public DeleteWorkspaceResult TryDelete(string name)
    {
        // Validate name
        if (!WorkspaceConfig.IsValidName(name, out var validationError))
        {
            return new DeleteWorkspaceResult { Success = false, Error = validationError };
        }

        // Check if in use
        lock (_lock)
        {
            if (_activeWorkspace == name)
            {
                return new DeleteWorkspaceResult
                {
                    Success = false,
                    Error = "Workspace is currently in use by a running job."
                };
            }
        }

        var workDir = Path.Combine(_config.WorkspacePath, name);

        // Path safety check
        var resolvedPath = Path.GetFullPath(workDir);
        var workspaceRoot = Path.GetFullPath(_config.WorkspacePath);
        if (!resolvedPath.StartsWith(workspaceRoot + Path.DirectorySeparatorChar) &&
            resolvedPath != workspaceRoot)
        {
            return new DeleteWorkspaceResult
            {
                Success = false,
                Error = "Workspace path is outside the workspace root."
            };
        }

        if (!Directory.Exists(workDir))
        {
            return new DeleteWorkspaceResult
            {
                Success = false,
                Error = $"Workspace '{name}' does not exist."
            };
        }

        try
        {
            // Normalize file attributes before deletion (handles git read-only files)
            SetAttributesNormal(new DirectoryInfo(workDir));
            Directory.Delete(workDir, recursive: true);
            _logger.LogInformation("Deleted workspace: {WorkspaceName}", name);
            return new DeleteWorkspaceResult { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete workspace: {WorkspaceName}", name);
            return new DeleteWorkspaceResult
            {
                Success = false,
                Error = $"Failed to delete workspace: {ex.Message}"
            };
        }
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
