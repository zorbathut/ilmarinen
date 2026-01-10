using System.Text;
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
    private readonly Func<string, string?> _secretProvider;
    private readonly List<string> _serviceContainerIds = [];

    public string Branch { get; }
    public string Commit { get; }

    public DockerJobContext(
        DockerClient client,
        string containerId,
        string workDir,
        string branch,
        string commit,
        Func<string, string?> secretProvider)
    {
        _client = client;
        _containerId = containerId;
        _workDir = workDir;
        Branch = branch;
        Commit = commit;
        _secretProvider = secretProvider;
    }

    public async Task<CommandResult> Exec(string command, params string[] args)
    {
        var cmd = new List<string> { command };
        cmd.AddRange(args);

        var execCreate = await _client.Exec.ExecCreateContainerAsync(_containerId, new ContainerExecCreateParameters
        {
            Cmd = cmd,
            AttachStdout = true,
            AttachStderr = true,
            WorkingDir = _workDir
        });

        using var stream = await _client.Exec.StartAndAttachContainerExecAsync(execCreate.ID, false);

        var (stdout, stderr) = await stream.ReadOutputToEndAsync(default);

        var inspect = await _client.Exec.InspectContainerExecAsync(execCreate.ID);

        // Print output
        if (!string.IsNullOrEmpty(stdout))
            Console.Write(stdout);
        if (!string.IsNullOrEmpty(stderr))
            Console.Error.Write(stderr);

        return new CommandResult
        {
            ExitCode = (int)inspect.ExitCode,
            Stdout = stdout,
            Stderr = stderr
        };
    }

    public Task<CommandResult> Shell(string script)
    {
        return Exec("/bin/sh", "-c", script);
    }

    public string Secret(string name)
    {
        return _secretProvider(name)
            ?? throw new InvalidOperationException($"Secret '{name}' not found");
    }

    public async Task<ImageRef> BuildImage(string dockerfile, string? tag = null)
    {
        var imageTag = tag ?? $"ilmarinen-build:{Guid.NewGuid():N}";

        // Use docker build via exec in the container (requires docker socket mount)
        var result = await Shell($"docker build -f {dockerfile} -t {imageTag} .");

        if (!result.Success)
        {
            throw new InvalidOperationException($"Failed to build image: {result.Stderr}");
        }

        return ImageRef.From(imageTag);
    }

    public async Task<CommandResult> Run(ImageRef image, params string[] command)
    {
        var cmdStr = string.Join(" ", command.Select(c => c.Contains(' ') ? $"\"{c}\"" : c));
        return await Shell($"docker run --rm {image.Reference} {cmdStr}");
    }

    public async Task<ServiceHandle> StartService(ImageRef image, string name, int[]? ports = null)
    {
        var portsArg = ports != null ? string.Join(" ", ports.Select(p => $"-p {p}")) : "";
        var result = await Shell($"docker run -d --name {name} {portsArg} {image.Reference}");

        if (!result.Success)
        {
            throw new InvalidOperationException($"Failed to start service: {result.Stderr}");
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
                var result = await Shell($"curl -sf {url}");
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
        await Shell($"docker stop {containerId}");
        await Shell($"docker rm {containerId}");
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
