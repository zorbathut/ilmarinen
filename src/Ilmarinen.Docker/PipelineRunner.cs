using System.Runtime.InteropServices;
using Docker.DotNet;
using Docker.DotNet.Models;
using Ilmarinen.Models;

namespace Ilmarinen.Docker;

/// <summary>
/// Runs pipeline steps using Docker.
/// </summary>
public class PipelineRunner
{
    private readonly DockerClient _client;
    private readonly string _workDir;
    private readonly Func<string, string?> _secretProvider;

    public PipelineRunner(string? workDir = null, Func<string, string?>? secretProvider = null)
    {
        _client = CreateDockerClient();
        _workDir = workDir ?? Directory.GetCurrentDirectory();
        _secretProvider = secretProvider ?? (name => Environment.GetEnvironmentVariable(name));
    }

    private static DockerClient CreateDockerClient()
    {
        var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");

        if (!string.IsNullOrEmpty(dockerHost))
        {
            return new DockerClientConfiguration(new Uri(dockerHost)).CreateClient();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new DockerClientConfiguration(new Uri("npipe://./pipe/docker_engine")).CreateClient();
        }

        return new DockerClientConfiguration(new Uri("unix:///var/run/docker.sock")).CreateClient();
    }

    public async Task<bool> RunAsync(IReadOnlyList<Step> steps)
    {
        var branch = await GetGitBranch();
        var commit = await GetGitCommit();

        Console.WriteLine($"Running {steps.Count} step(s)...");
        Console.WriteLine($"Branch: {branch}, Commit: {commit[..Math.Min(8, commit.Length)]}");
        Console.WriteLine();

        foreach (var step in steps)
        {
            Console.WriteLine($"=== Step: {step.Name} ===");
            Console.WriteLine($"Image: {step.Image}");

            try
            {
                await RunStepAsync(step, branch, commit);
                Console.WriteLine($"=== {step.Name}: SUCCESS ===");
                Console.WriteLine();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"=== {step.Name}: FAILED ===");
                Console.WriteLine($"Error: {ex.Message}");
                return false;
            }
        }

        Console.WriteLine("All steps completed successfully!");
        return true;
    }

    private async Task RunStepAsync(Step step, string branch, string commit)
    {
        // Pull the image if needed
        await PullImageIfNeeded(step.Image);

        // Create and start the container
        var containerId = await CreateContainerAsync(step.Image);

        try
        {
            await _client.Containers.StartContainerAsync(containerId, new ContainerStartParameters());

            var context = new DockerJobContext(
                _client,
                containerId,
                "/workspace",
                branch,
                commit,
                _secretProvider);

            try
            {
                await step.Action(context);
            }
            finally
            {
                await context.CleanupAsync();
            }
        }
        finally
        {
            // Stop and remove container
            await _client.Containers.StopContainerAsync(containerId, new ContainerStopParameters());
            await _client.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters());
        }
    }

    private async Task PullImageIfNeeded(string image)
    {
        try
        {
            await _client.Images.InspectImageAsync(image);
        }
        catch (DockerImageNotFoundException)
        {
            Console.WriteLine($"Pulling image: {image}");
            await _client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = image },
                null,
                new Progress<JSONMessage>(m =>
                {
                    if (!string.IsNullOrEmpty(m.Status))
                        Console.WriteLine($"  {m.Status}");
                }));
        }
    }

    private async Task<string> CreateContainerAsync(string image)
    {
        var response = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = image,
            Tty = true,
            AttachStdout = true,
            AttachStderr = true,
            WorkingDir = "/workspace",
            Cmd = ["/bin/sh", "-c", "tail -f /dev/null"], // Keep container running
            HostConfig = new HostConfig
            {
                Binds =
                [
                    $"{_workDir}:/workspace",
                    "/var/run/docker.sock:/var/run/docker.sock" // For nested containers
                ],
                AutoRemove = false
            }
        });

        return response.ID;
    }

    private async Task<string> GetGitBranch()
    {
        try
        {
            var gitDir = FindGitDir(_workDir);
            if (gitDir == null) return "unknown";

            var headPath = Path.Combine(gitDir, "HEAD");
            if (!File.Exists(headPath)) return "unknown";

            var head = await File.ReadAllTextAsync(headPath);
            if (head.StartsWith("ref: refs/heads/"))
            {
                return head["ref: refs/heads/".Length..].Trim();
            }
            return head.Trim()[..8]; // Detached HEAD, return short SHA
        }
        catch
        {
            return "unknown";
        }
    }

    private async Task<string> GetGitCommit()
    {
        try
        {
            var gitDir = FindGitDir(_workDir);
            if (gitDir == null) return "unknown";

            var headPath = Path.Combine(gitDir, "HEAD");
            if (!File.Exists(headPath)) return "unknown";

            var head = (await File.ReadAllTextAsync(headPath)).Trim();

            if (head.StartsWith("ref: "))
            {
                var refPath = Path.Combine(gitDir, head["ref: ".Length..]);
                if (File.Exists(refPath))
                {
                    return (await File.ReadAllTextAsync(refPath)).Trim();
                }
            }

            return head;
        }
        catch
        {
            return "unknown";
        }
    }

    private static string? FindGitDir(string startDir)
    {
        var dir = startDir;
        while (dir != null)
        {
            var gitDir = Path.Combine(dir, ".git");
            if (Directory.Exists(gitDir)) return gitDir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
