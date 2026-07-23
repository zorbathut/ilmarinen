using Docker.DotNet;
using Ilmarinen.Execution;
using Ilmarinen.Models;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Docker;

/// <summary>
/// IJobContext implementation that uses Docker.
/// </summary>
public class DockerJobContext : IJobContext
{
    private readonly DockerClient _client;
    private readonly string _containerId;
    private readonly string _containerWorkDir;
    private readonly string _localWorkDir;
    private readonly string _hostWorkDir;
    private readonly string _networkName;
    private readonly Func<string, string?> _secretProvider;
    private readonly Func<string, string?, Task<ArtifactRef>>? _artifactSaver;
    private readonly Action<string, string>? _onOutput;
    private readonly string? _userSpec;
    private readonly uint? _dockerSocketGid;
    private readonly List<string> _serviceContainerIds = [];

    public string Branch { get; }
    public string Commit { get; }

    /// <summary>
    /// Creates a job context. The three workdir parameters are three path perspectives on the same workspace; localWorkDir and hostWorkDir differ when this process itself runs in a container.
    /// </summary>
    /// <param name="containerWorkDir">Workspace path inside the job container (used as the exec working directory)</param>
    /// <param name="localWorkDir">Workspace path on this process's own filesystem (used for direct file access like artifact reads)</param>
    /// <param name="hostWorkDir">Workspace path as seen by the Docker daemon's host (used for -v bind mount arguments)</param>
    public DockerJobContext(
        DockerClient client,
        string containerId,
        string containerWorkDir,
        string localWorkDir,
        string hostWorkDir,
        string networkName,
        string branch,
        string commit,
        Func<string, string?> secretProvider,
        Func<string, string?, Task<ArtifactRef>>? artifactSaver = null,
        Action<string, string>? onOutput = null,
        string? userSpec = null,
        uint? dockerSocketGid = null)
    {
        _client = client;
        _containerId = containerId;
        _containerWorkDir = containerWorkDir;
        _localWorkDir = localWorkDir;
        _hostWorkDir = hostWorkDir;
        _networkName = networkName;
        Branch = branch;
        Commit = commit;
        _secretProvider = secretProvider;
        _artifactSaver = artifactSaver;
        _onOutput = onOutput;
        _userSpec = userSpec;
        _dockerSocketGid = dockerSocketGid;
    }

    public async Task<CommandResult> TryExec(string command, params string[] args)
    {
        var cmd = new List<string> { command };
        cmd.AddRange(args);

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        var exitCode = await DockerExec.RunAsync(_client, _containerId, cmd, _containerWorkDir, _userSpec, (isStdout, chunk) =>
        {
            if (isStdout)
            {
                stdoutBuilder.Append(chunk);
                Console.Write(chunk);
                _onOutput?.Invoke("o", chunk);
            }
            else
            {
                stderrBuilder.Append(chunk);
                Console.Error.Write(chunk);
                _onOutput?.Invoke("e", chunk);
            }
            return Task.CompletedTask;
        });

        return new CommandResult
        {
            ExitCode = exitCode,
            Stdout = stdoutBuilder.ToString(),
            Stderr = stderrBuilder.ToString()
        };
    }

    public async Task<CommandResult> Exec(string command, params string[] args)
    {
        var result = await TryExec(command, args);
        if (!result.Success)
        {
            throw new CommandException(command, args, result.ExitCode, result.Stdout, result.Stderr, result);
        }
        return result;
    }

    public Task<CommandResult> TryShell(string script)
    {
        return TryExec("/bin/sh", "-c", script);
    }

    /// <summary>
    /// Executes a command with streaming output to the provided stream in NDJSON format.
    /// Each line is a JSON object: {"t":"o","d":"..."} for stdout, {"t":"e","d":"..."} for stderr,
    /// or {"t":"x","c":0} for exit.
    /// </summary>
    private async Task<CommandResult> TryExecStreaming(Stream outputStream, string command, params string[] args)
    {
        var cmd = new List<string> { command };
        cmd.AddRange(args);

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        var writer = new StreamWriter(outputStream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

        var exitCode = await DockerExec.RunAsync(_client, _containerId, cmd, _containerWorkDir, _userSpec, async (isStdout, chunk) =>
        {
            // Write NDJSON line to stream
            var json = JsonSerializer.Serialize(new { t = isStdout ? "o" : "e", d = chunk });
            await writer.WriteLineAsync(json);

            // Accumulate for final CommandResult
            if (isStdout)
            {
                stdoutBuilder.Append(chunk);
                Console.Write(chunk);
                _onOutput?.Invoke("o", chunk);
            }
            else
            {
                stderrBuilder.Append(chunk);
                Console.Error.Write(chunk);
                _onOutput?.Invoke("e", chunk);
            }
        });

        // Write exit message
        var exitJson = JsonSerializer.Serialize(new { t = "x", c = exitCode });
        await writer.WriteLineAsync(exitJson);

        return new CommandResult
        {
            ExitCode = exitCode,
            Stdout = stdoutBuilder.ToString(),
            Stderr = stderrBuilder.ToString()
        };
    }

    private Task<CommandResult> TryShellStreaming(Stream outputStream, string script)
    {
        return TryExecStreaming(outputStream, "/bin/sh", "-c", script);
    }

    /// <summary>
    /// Builds the docker run command with user/group and workspace settings.
    /// </summary>
    internal string BuildDockerRunCommand(ImageRef image, string[] command)
    {
        var cmdStr = string.Join(" ", command.Select(EscapeShellArg));
        var userArg = _userSpec != null ? $"--user {_userSpec} " : "";
        var groupArg = _dockerSocketGid.HasValue ? $"--group-add {_dockerSocketGid.Value} " : "";
        // Set HOME and common cache dirs to /tmp when running as non-root (no passwd entry for the UID)
        var envArg = _userSpec != null ? "-e HOME=/tmp -e DOCKER_CONFIG=/tmp/.docker " : "";
        return $"docker run --rm {userArg}{groupArg}{envArg}-v {_hostWorkDir}:/workspace -w /workspace --network {_networkName} {EscapeShellArg(image.Reference)} {cmdStr}";
    }

    /// <summary>
    /// Runs a nested container with streaming output to the provided stream.
    /// </summary>
    private async Task<CommandResult> TryRunStreaming(Stream outputStream, ImageRef image, params string[] command)
    {
        return await TryShellStreaming(outputStream, BuildDockerRunCommand(image, command));
    }

    public async Task<CommandResult> RunStreaming(Stream outputStream, ImageRef image, params string[] command)
    {
        var result = await TryRunStreaming(outputStream, image, command);
        if (!result.Success)
        {
            throw new NestedContainerException(
                image.Reference,
                command,
                result.ExitCode,
                result.Stdout,
                result.Stderr);
        }
        return result;
    }

    public async Task<CommandResult> Shell(string script)
    {
        var result = await TryShell(script);
        if (!result.Success)
        {
            throw new ShellException(script, result.ExitCode, result.Stdout, result.Stderr, result);
        }
        return result;
    }

    public string Secret(string name)
    {
        return _secretProvider(name)
            ?? throw new InvalidOperationException($"Secret '{name}' not found");
    }

    public async Task<ImageRef> BuildImage(string dockerfile, string? tag = null, string? context = null, IDictionary<string, string>? buildArgs = null)
    {
        var imageTag = tag ?? $"ilmarinen-build:{Guid.NewGuid():N}";
        var buildContext = context ?? ".";

        // Escape paths for shell (handle spaces and special characters)
        var escapedDockerfile = EscapeShellArg(dockerfile);
        var escapedContext = EscapeShellArg(buildContext);

        // Build the --build-arg flags
        var buildArgsStr = "";
        if (buildArgs != null)
        {
            foreach (var (key, value) in buildArgs)
            {
                buildArgsStr += $" --build-arg {EscapeShellArg($"{key}={value}")}";
            }
        }

        // Use docker build via exec in the container (requires docker socket mount)
        // Shell throws ShellException on failure
        await Shell($"docker build -f {escapedDockerfile} -t {imageTag}{buildArgsStr} {escapedContext}");

        return ImageRef.From(imageTag);
    }

    /// <summary>
    /// Escapes a string for safe use as a shell argument.
    /// </summary>
    internal static string EscapeShellArg(string arg)
    {
        // Always single-quote — the string is evaluated by /bin/sh -c, and any unquoted arg is one metacharacter (;, *, ~, $?, ...) away from being split or expanded by that outer shell; an empty arg would vanish from argv entirely. Don't reintroduce a "no special characters" fast path: enumerating the safe set is exactly how this went wrong before.
        return "'" + arg.Replace("'", "'\\''") + "'";
    }

    public async Task<CommandResult> TryRun(ImageRef image, params string[] command)
    {
        return await TryShell(BuildDockerRunCommand(image, command));
    }

    public async Task<CommandResult> Run(ImageRef image, params string[] command)
    {
        var result = await TryRun(image, command);
        if (!result.Success)
        {
            throw new NestedContainerException(
                image.Reference,
                command,
                result.ExitCode,
                result.Stdout,
                result.Stderr);
        }
        return result;
    }

    public async Task<IServiceHandle> StartService(ImageRef image, string name, int[]? ports = null)
    {
        // Prefix container name with network name to avoid collisions between concurrent/stale runs
        var containerName = $"{_networkName}-{name}";
        var portsArg = ports != null ? string.Join(" ", ports.Select(p => $"-p {p}")) : "";
        // Connect to network so services can communicate
        var result = await TryShell($"docker run -d --name {containerName} --network {_networkName} {portsArg} {image.Reference}");

        if (!result.Success)
        {
            throw new ShellException(
                $"docker run -d --name {containerName} --network {_networkName} {portsArg} {image.Reference}",
                result.ExitCode,
                result.Stdout,
                result.Stderr,
                result);
        }

        var containerId = result.Stdout.Trim();
        _serviceContainerIds.Add(containerId);

        return new DockerServiceHandle(_client, containerId, containerName, this);
    }

    public async Task WaitForHealthy(string url, TimeSpan? timeout = null)
    {
        var actualTimeout = timeout ?? TimeSpan.FromSeconds(60);
        var deadline = DateTime.UtcNow + actualTimeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                CommandResult result;
                if (url.StartsWith("tcp://"))
                {
                    // TCP health check using nc (netcat)
                    var parts = url["tcp://".Length..].Split(':');
                    var host = parts[0];
                    var port = parts.Length > 1 ? parts[1] : "80";
                    result = await TryShell($"nc -z {host} {port}");
                }
                else
                {
                    // HTTP health check using curl
                    result = await TryShell($"curl -sf {url}");
                }

                if (result.Success)
                    return;
            }
            catch
            {
                // Ignore and retry
            }

            await Task.Delay(1000);
        }

        throw new TimeoutException($"Service at {url} did not become healthy within {actualTimeout}");
    }

    public async Task<ArtifactRef> SaveArtifact(string path, string? name = null)
    {
        if (_artifactSaver == null)
        {
            throw new InvalidOperationException("Artifact saving is not configured for this context");
        }

        // Resolve path - relative paths are relative to /workspace. The file is read by this process, so resolve against _localWorkDir, not _hostWorkDir (which is only meaningful to the Docker daemon and may not exist on our filesystem when we run in a container).
        string localPath;
        if (path.StartsWith("/workspace/"))
        {
            localPath = Path.Combine(_localWorkDir, path["/workspace/".Length..]);
        }
        else if (path.StartsWith("/"))
        {
            // Absolute path outside /workspace - not supported via bind mount
            throw new ArgumentException($"Cannot save artifact from absolute path outside /workspace: {path}");
        }
        else
        {
            // Relative path
            localPath = Path.Combine(_localWorkDir, path);
        }

        if (!File.Exists(localPath))
        {
            throw new FileNotFoundException($"Artifact file not found: {path}", localPath);
        }

        return await _artifactSaver(localPath, name);
    }

    internal async Task StopServiceAsync(string containerId)
    {
        // Best effort cleanup - use TryShell to avoid throwing
        await TryShell($"docker stop {containerId}");
        await TryShell($"docker rm {containerId}");
        _serviceContainerIds.Remove(containerId);
    }

    public async Task CleanupAsync()
    {
        foreach (var id in _serviceContainerIds.ToList())
        {
            try
            {
                await StopServiceAsync(id);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }
}

internal class DockerServiceHandle : IServiceHandle
{
    private readonly DockerClient _client;
    private readonly string _containerId;
    private readonly DockerJobContext _context;

    public string Name { get; }

    public DockerServiceHandle(DockerClient client, string containerId, string name, DockerJobContext context)
    {
        _client = client;
        _containerId = containerId;
        Name = name;
        _context = context;
    }

    public async Task StopAsync()
    {
        await _context.StopServiceAsync(_containerId);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
