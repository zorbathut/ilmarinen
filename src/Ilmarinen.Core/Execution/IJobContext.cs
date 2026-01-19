using Ilmarinen.Models;

namespace Ilmarinen.Execution;

/// <summary>
/// Context passed to step actions for interacting with the container.
/// </summary>
public interface IJobContext
{
    /// <summary>
    /// The current git branch.
    /// </summary>
    string Branch { get; }

    /// <summary>
    /// The current git commit SHA.
    /// </summary>
    string Commit { get; }

    /// <summary>
    /// Execute a command in the container.
    /// Throws <see cref="CommandException"/> on non-zero exit code.
    /// </summary>
    /// <exception cref="CommandException">When command fails with non-zero exit code.</exception>
    Task<CommandResult> Exec(string command, params string[] args);

    /// <summary>
    /// Execute a command in the container without throwing on failure.
    /// </summary>
    Task<CommandResult> TryExec(string command, params string[] args);

    /// <summary>
    /// Execute a shell script in the container.
    /// Throws <see cref="ShellException"/> on non-zero exit code.
    /// </summary>
    /// <exception cref="ShellException">When script fails with non-zero exit code.</exception>
    Task<CommandResult> Shell(string script);

    /// <summary>
    /// Execute a shell script in the container without throwing on failure.
    /// </summary>
    Task<CommandResult> TryShell(string script);

    /// <summary>
    /// Get a secret value by name.
    /// </summary>
    string Secret(string name);

    /// <summary>
    /// Build a container image from a Dockerfile.
    /// </summary>
    /// <param name="dockerfile">Path to the Dockerfile (relative to workspace or absolute)</param>
    /// <param name="tag">Optional image tag. If not provided, a random tag is generated.</param>
    /// <param name="context">Build context directory. Defaults to current directory "."</param>
    /// <param name="buildArgs">Optional build arguments to pass to docker build.</param>
    Task<ImageRef> BuildImage(string dockerfile, string? tag = null, string? context = null, IDictionary<string, string>? buildArgs = null);

    /// <summary>
    /// Run a container and wait for it to complete.
    /// Throws <see cref="NestedContainerException"/> on non-zero exit code.
    /// </summary>
    /// <exception cref="NestedContainerException">When container fails with non-zero exit code.</exception>
    Task<CommandResult> Run(ImageRef image, params string[] command);

    /// <summary>
    /// Run a container and wait for it to complete without throwing on failure.
    /// </summary>
    Task<CommandResult> TryRun(ImageRef image, params string[] command);

    /// <summary>
    /// Start a service container (runs in background).
    /// </summary>
    Task<ServiceHandle> StartService(ImageRef image, string name, int[]? ports = null);

    /// <summary>
    /// Wait for a service to be healthy.
    /// </summary>
    Task WaitForHealthy(string url, TimeSpan? timeout = null);

    /// <summary>
    /// Save a file from the container as an artifact.
    /// </summary>
    /// <param name="path">Path to the file (relative to /workspace or absolute)</param>
    /// <param name="name">Optional artifact name. Defaults to the filename.</param>
    Task<ArtifactRef> SaveArtifact(string path, string? name = null);
}

/// <summary>
/// Handle to a running service container.
/// </summary>
public interface IServiceHandle : IAsyncDisposable
{
    /// <summary>
    /// The service name/hostname.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Stop the service.
    /// </summary>
    Task StopAsync();
}

/// <summary>
/// Alias for IServiceHandle.
/// </summary>
public interface ServiceHandle : IServiceHandle { }

/// <summary>
/// Result of executing a command.
/// </summary>
public sealed record CommandResult
{
    public required int ExitCode { get; init; }
    public required string Stdout { get; init; }
    public required string Stderr { get; init; }
    public bool Success => ExitCode == 0;

    public static CommandResult Ok(string stdout = "", string stderr = "") => new()
    {
        ExitCode = 0,
        Stdout = stdout,
        Stderr = stderr
    };

    public static CommandResult Failed(int exitCode, string stdout = "", string stderr = "") => new()
    {
        ExitCode = exitCode,
        Stdout = stdout,
        Stderr = stderr
    };
}
