using Ilmarinen.Models;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.Worker.Services;

public class WorkspaceManager
{
    private const string MirrorsDirName = ".mirrors";

    /// <summary>
    /// Long enough to walk any real workspace; a find still running past it is stuck (on a dead mount, say) and would hold up every later measurement.
    /// </summary>
    private static readonly TimeSpan MeasureTimeout = TimeSpan.FromMinutes(10);

    private readonly WorkerConfig _config;
    private readonly ILogger<WorkspaceManager> _logger;
    private string? _activeWorkspace;
    private readonly object _lock = new();

    // Paths whose last measurement failed, so a failure that persists warns once rather than on every pass. Only the measuring loop touches it.
    private readonly HashSet<string> _unmeasurable = new();

    // The latest measurement's sizes, for choosing what to delete to make room.
    private volatile IReadOnlyDictionary<string, long?> _lastSizes = new Dictionary<string, long?>();

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
    /// Measures the disk space each workspace would free if deleted, and the space the git mirrors take.
    /// </summary>
    public async Task<WorkspaceUsageReport> MeasureUsageAsync(CancellationToken cancellationToken)
    {
        var sizes = new Dictionary<string, long?>();
        foreach (var workspace in DiscoverWorkspaces())
        {
            var usage = await MeasureAsync(workspace.Path, cancellationToken);

            // Deleted while it was being walked: find saw nothing, which is no measurement of anything
            if (Directory.Exists(workspace.Path))
            {
                sizes[workspace.Name] = usage?.FreedBytes;
            }
        }

        var mirrors = Path.Combine(_config.WorkspacePath, MirrorsDirName);
        long? mirrorsSize = Directory.Exists(mirrors) ? (await MeasureAsync(mirrors, cancellationToken))?.TotalBytes : 0;

        _lastSizes = sizes;
        return new WorkspaceUsageReport { WorkspaceSizes = sizes, MirrorsSizeBytes = mirrorsSize };
    }

    /// <summary>The workspaces that could be deleted to make room, each with its last measured size; all but <paramref name="keep"/>.</summary>
    public IReadOnlyList<RoomCandidate> RoomCandidates(string? keep)
    {
        var sizes = _lastSizes;
        return DiscoverWorkspaces()
            .Where(w => w.Name != keep)
            .Select(w => new RoomCandidate(w.Name, w.LastUsed, sizes.GetValueOrDefault(w.Name)))
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

    /// <summary>
    /// Walks <paramref name="path"/> with GNU find (for its -printf; the worker image is Debian) and returns both its plain disk usage and the part of it that deleting it would free: blocks of files every one of whose hard links is inside it. A local clone hard-links its mirror's objects, and clones of one mirror can go on sharing them after the mirror is rebuilt, so the two differ. Null if it can't be measured.
    /// </summary>
    private async Task<(long TotalBytes, long FreedBytes)?> MeasureAsync(string path, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("find")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        // Type, device and inode, link count, 512-byte blocks; no names, so nothing in a path can upset the parse
        startInfo.ArgumentList.Add("-P");
        startInfo.ArgumentList.Add(path);
        startInfo.ArgumentList.Add("-printf");
        startInfo.ArgumentList.Add("%y %D %i %n %b\\0");
        // Untranslated error messages, so a vanished file can be told apart from a real failure
        startInfo.Environment["LC_ALL"] = "C";

        Process process;
        try
        {
            process = Process.Start(startInfo)!;
        }
        catch (Win32Exception ex)
        {
            return MeasurementFailed(path, $"could not run find: {ex.Message}");
        }

        using (process)
        {
            try
            {
                // Lowers CPU priority only; the BFQ I/O scheduler derives an I/O priority from it, others don't
                process.PriorityClass = ProcessPriorityClass.Idle;
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogDebug(ex, "find for {Path} exited before it could be deprioritized", path);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(MeasureTimeout);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            BlockAccount account;
            try
            {
                account = await BlockAccount.ReadAsync(process.StandardOutput, timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                await stderrTask;
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                return MeasurementFailed(path, $"find was still running after {MeasureTimeout.TotalMinutes} minutes and was killed");
            }
            catch (FormatException ex)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                await stderrTask;
                return MeasurementFailed(path, ex.Message);
            }

            var stderr = await stderrTask;

            // A file deleted mid-walk (a running job's temp files, say) is one deleting the workspace wouldn't need to free; anything else unreadable would make the total an undercount
            var errors = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line => !line.EndsWith("No such file or directory"))
                .ToList();
            if (process.ExitCode != 0 && (errors.Count > 0 || stderr.Length == 0))
            {
                return MeasurementFailed(path, $"find exited {process.ExitCode}: {string.Join(" / ", errors)}");
            }

            if (_unmeasurable.Remove(path))
            {
                _logger.LogInformation("Disk usage of {Path} can be measured again", path);
            }
            return (account.TotalBlocks * 512, account.FreedBlocks * 512);
        }
    }

    private (long TotalBytes, long FreedBytes)? MeasurementFailed(string path, string reason)
    {
        if (_unmeasurable.Add(path))
        {
            _logger.LogWarning("Could not measure disk usage of {Path}: {Reason}", path, reason);
        }
        else
        {
            _logger.LogDebug("Still could not measure disk usage of {Path}: {Reason}", path, reason);
        }
        return null;
    }
}

/// <summary>
/// Tallies find's records as they stream in, so a tree of millions of files never sits in memory at once. Only files with several links need remembering — a file with one link is wholly inside the walked tree.
/// </summary>
internal sealed class BlockAccount
{
    private readonly Dictionary<(long Device, long Inode), (long Links, long Blocks, long Seen)> _shared = new();
    private long _ownBlocks;

    public long TotalBlocks { get; private set; }

    public long FreedBlocks
    {
        get { return _ownBlocks + _shared.Values.Where(f => f.Seen >= f.Links).Sum(f => f.Blocks); }
    }

    public static async Task<BlockAccount> ReadAsync(TextReader reader, CancellationToken cancellationToken)
    {
        var account = new BlockAccount();
        var buffer = new char[64 * 1024];
        var record = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == '\0')
                {
                    account.Add(record.ToString());
                    record.Clear();
                }
                else
                {
                    record.Append(buffer[i]);
                }
            }
        }
        if (record.Length > 0)
        {
            throw new FormatException($"truncated find record: {record}");
        }
        return account;
    }

    /// <summary>
    /// One "type device inode links blocks" record.
    /// </summary>
    public void Add(string record)
    {
        var fields = record.Split(' ');
        if (fields.Length != 5
            || !long.TryParse(fields[1], CultureInfo.InvariantCulture, out var device)
            || !long.TryParse(fields[2], CultureInfo.InvariantCulture, out var inode)
            || !long.TryParse(fields[3], CultureInfo.InvariantCulture, out var links)
            || !long.TryParse(fields[4], CultureInfo.InvariantCulture, out var blocks))
        {
            throw new FormatException($"unexpected find record: {record}");
        }

        // A directory's link count counts its subdirectories, not other names for it; its blocks are always its own
        if (fields[0] == "d" || links <= 1)
        {
            _ownBlocks += blocks;
            TotalBlocks += blocks;
            return;
        }

        if (_shared.TryGetValue((device, inode), out var seen))
        {
            _shared[(device, inode)] = seen with { Seen = seen.Seen + 1 };
        }
        else
        {
            _shared[(device, inode)] = (links, blocks, 1);
            TotalBlocks += blocks;
        }
    }
}
