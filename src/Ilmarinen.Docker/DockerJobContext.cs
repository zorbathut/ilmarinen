using System.Text;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using Ilmarinen.Execution;
using Ilmarinen.Models;

namespace Ilmarinen.Docker;

/// <summary>
/// IJobContext implementation that uses Docker.
/// </summary>
public class DockerJobContext : IJobContext
{
    private readonly DockerClient _client;
    private readonly string _containerId;
    private readonly string _workDir;
    private readonly string _hostWorkDir;
    private readonly string _networkName;
    private readonly Func<string, string?> _secretProvider;
    private readonly Action<string, string>? _onOutput;
    private readonly string? _userSpec;
    private readonly uint? _dockerSocketGid;
    private readonly List<string> _serviceContainerIds = [];

    public string Branch { get; }
    public string Commit { get; }

    public DockerJobContext(
        DockerClient client,
        string containerId,
        string workDir,
        string hostWorkDir,
        string networkName,
        string branch,
        string commit,
        Func<string, string?> secretProvider,
        Action<string, string>? onOutput = null,
        string? userSpec = null,
        uint? dockerSocketGid = null)
    {
        _client = client;
        _containerId = containerId;
        _workDir = workDir;
        _hostWorkDir = hostWorkDir;
        _networkName = networkName;
        Branch = branch;
        Commit = commit;
        _secretProvider = secretProvider;
        _onOutput = onOutput;
        _userSpec = userSpec;
        _dockerSocketGid = dockerSocketGid;
    }

    public async Task<CommandResult> TryExec(string command, params string[] args)
    {
        var cmd = new List<string> { command };
        cmd.AddRange(args);

        var execCreate = await _client.Exec.ExecCreateContainerAsync(_containerId, new ContainerExecCreateParameters
        {
            Cmd = cmd,
            AttachStdout = true,
            AttachStderr = true,
            WorkingDir = _workDir,
            User = _userSpec
        });

        using var multiplexed = await _client.Exec.StartAndAttachContainerExecAsync(execCreate.ID, false);

        // Stream output in real-time
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        var buffer = new byte[4096];

        while (true)
        {
            var result = await multiplexed.ReadOutputAsync(buffer, 0, buffer.Length, default);
            if (result.EOF) break;

            var chunk = Encoding.UTF8.GetString(buffer, 0, result.Count);
            if (result.Target == MultiplexedStream.TargetStream.StandardOut)
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
        }

        var inspect = await _client.Exec.InspectContainerExecAsync(execCreate.ID);

        return new CommandResult
        {
            ExitCode = (int)inspect.ExitCode,
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
    public async Task<CommandResult> TryExecStreaming(Stream outputStream, string command, params string[] args)
    {
        var cmd = new List<string> { command };
        cmd.AddRange(args);

        var execCreate = await _client.Exec.ExecCreateContainerAsync(_containerId, new ContainerExecCreateParameters
        {
            Cmd = cmd,
            AttachStdout = true,
            AttachStderr = true,
            WorkingDir = _workDir,
            User = _userSpec
        });

        using var multiplexed = await _client.Exec.StartAndAttachContainerExecAsync(execCreate.ID, false);

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        var buffer = new byte[4096];
        var writer = new StreamWriter(outputStream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

        while (true)
        {
            var result = await multiplexed.ReadOutputAsync(buffer, 0, buffer.Length, default);
            if (result.EOF) break;

            var chunk = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var isStdout = result.Target == MultiplexedStream.TargetStream.StandardOut;
            var type = isStdout ? "o" : "e";

            // Write NDJSON line to stream
            var json = JsonSerializer.Serialize(new { t = type, d = chunk });
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
        }

        var inspect = await _client.Exec.InspectContainerExecAsync(execCreate.ID);
        var exitCode = (int)inspect.ExitCode;

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

    public Task<CommandResult> TryShellStreaming(Stream outputStream, string script)
    {
        return TryExecStreaming(outputStream, "/bin/sh", "-c", script);
    }

    /// <summary>
    /// Builds the docker run command with user/group and workspace settings.
    /// </summary>
    private string BuildDockerRunCommand(ImageRef image, string[] command)
    {
        var cmdStr = string.Join(" ", command.Select(c => c.Contains(' ') ? $"\"{c}\"" : c));
        var userArg = _userSpec != null ? $"--user {_userSpec} " : "";
        var groupArg = _dockerSocketGid.HasValue ? $"--group-add {_dockerSocketGid.Value} " : "";
        // Set HOME and common cache dirs to /tmp when running as non-root (no passwd entry for the UID)
        var envArg = _userSpec != null ? "-e HOME=/tmp -e DOCKER_CONFIG=/tmp/.docker " : "";
        return $"docker run --rm {userArg}{groupArg}{envArg}-v {_hostWorkDir}:/workspace -w /workspace --network {_networkName} {image.Reference} {cmdStr}";
    }

    /// <summary>
    /// Runs a nested container with streaming output to the provided stream.
    /// </summary>
    public async Task<CommandResult> TryRunStreaming(Stream outputStream, ImageRef image, params string[] command)
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
    private static string EscapeShellArg(string arg)
    {
        // If no special characters, return as-is
        if (!arg.Any(c => char.IsWhiteSpace(c) || c == '\'' || c == '"' || c == '\\' || c == '$' || c == '`'))
            return arg;

        // Use single quotes and escape any single quotes within
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

    public async Task<ServiceHandle> StartService(ImageRef image, string name, int[]? ports = null)
    {
        var portsArg = ports != null ? string.Join(" ", ports.Select(p => $"-p {p}")) : "";
        // Connect to network so services can communicate
        var result = await TryShell($"docker run -d --name {name} --network {_networkName} {portsArg} {image.Reference}");

        if (!result.Success)
        {
            throw new ShellException(
                $"docker run -d --name {name} --network {_networkName} {portsArg} {image.Reference}",
                result.ExitCode,
                result.Stdout,
                result.Stderr,
                result);
        }

        var containerId = result.Stdout.Trim();
        _serviceContainerIds.Add(containerId);

        return new DockerServiceHandle(_client, containerId, name, this);
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

internal class DockerServiceHandle : ServiceHandle
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
