using Ilmarinen.Models;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.IO;
using System;
using System.Linq;

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
    /// Lists persistent workspaces with their full local paths and last use (filters out GUID-named job dirs and dot-named worker state such as the git mirrors).
    /// </summary>
    public List<WorkspaceInfo> DiscoverWorkspaces()
    {
        var workspacePath = _config.WorkspacePath;
        if (!Directory.Exists(workspacePath))
            return [];

        return Directory.GetDirectories(workspacePath)
            .Where(dir => !Guid.TryParse(Path.GetFileName(dir), out _) && !Path.GetFileName(dir).StartsWith('.'))
            .Select(dir => new WorkspaceInfo
            {
                Name = Path.GetFileName(dir),
                Path = Path.GetFullPath(dir),
                LastUsed = Directory.GetLastWriteTimeUtc(dir)
            })
            .ToList();
    }

    /// <summary>
    /// Tracks which workspace the current job is using.
    /// </summary>
    public void SetActiveWorkspace(string name)
    {
        lock (_lock)
        {
            _activeWorkspace = name;
        }
    }

    /// <summary>
    /// Ends the current job's use of its workspace: if the job got as far as creating it, this becomes its last use, and it may be deleted again.
    /// </summary>
    public void ReleaseActiveWorkspace()
    {
        lock (_lock)
        {
            // Stamped while still active, so a delete can't remove the workspace in between and leave the stamp nowhere
            var name = _activeWorkspace;
            if (name != null)
            {
                StampLastUse(name);
            }
            _activeWorkspace = null;
        }
    }

    /// <summary>
    /// A workspace's last use is its directory's modification time, set when a job finishes with it, so the record lives and dies with the workspace.
    /// </summary>
    private void StampLastUse(string name)
    {
        var dir = Path.Combine(_config.WorkspacePath, name);
        if (!Directory.Exists(dir))
        {
            return;
        }

        try
        {
            Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to record last use of workspace {WorkspaceName}", name);
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
            return new DeleteWorkspaceResult { Status = DeleteWorkspaceStatus.Error, Error = validationError };
        }

        // Check if in use
        lock (_lock)
        {
            if (_activeWorkspace == name)
            {
                return new DeleteWorkspaceResult
                {
                    Status = DeleteWorkspaceStatus.InUse,
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
                Status = DeleteWorkspaceStatus.Error,
                Error = "Workspace path is outside the workspace root."
            };
        }

        if (!Directory.Exists(workDir))
        {
            return new DeleteWorkspaceResult
            {
                Status = DeleteWorkspaceStatus.NotFound,
                Error = $"Workspace '{name}' does not exist."
            };
        }

        try
        {
            // Normalize file attributes before deletion (handles git read-only files)
            SetAttributesNormal(new DirectoryInfo(workDir));
            Directory.Delete(workDir, recursive: true);
            _logger.LogInformation("Deleted workspace: {WorkspaceName}", name);
            return new DeleteWorkspaceResult { Status = DeleteWorkspaceStatus.Success };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete workspace: {WorkspaceName}", name);
            return new DeleteWorkspaceResult
            {
                Status = DeleteWorkspaceStatus.Error,
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
