using Docker.DotNet.Models;
using Docker.DotNet;
using Ilmarinen.Models;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Docker;

/// <summary>
/// Runs pipeline steps using Docker.
/// </summary>
public class PipelineRunner : IDisposable
{
    private readonly DockerClient _client;
    private readonly string _workDir;
    private readonly string _hostWorkDir;
    private readonly string? _workerContainerId;
    private readonly Func<string, string?> _secretProvider;
    private readonly Func<string, string?, Task<ArtifactRef>>? _artifactSaver;
    private readonly Action<string, string>? _onOutput;
    private readonly string? _userSpec;
    private readonly uint? _dockerSocketGid;

    /// <summary>
    /// Creates a new PipelineRunner.
    /// </summary>
    /// <param name="workDir">Working directory for local file operations (defaults to current directory)</param>
    /// <param name="hostWorkDir">Host-side path for Docker bind mounts. When running in Docker, this should be the
    /// actual host path that maps to workDir. Defaults to workDir (correct for non-containerized execution).</param>
    /// <param name="workerContainerId">Container ID of the worker when running in Docker-in-Docker.
    /// Used to connect the worker to job networks for AgentApiServer communication.</param>
    /// <param name="secretProvider">Function to resolve secrets (defaults to environment variables)</param>
    /// <param name="artifactSaver">Optional custom artifact saver. If not provided, uses local filesystem storage.</param>
    /// <param name="onOutput">Optional callback for log output. First parameter is type ("o" for stdout, "e" for stderr), second is data.</param>
    public PipelineRunner(
        string? workDir = null,
        string? hostWorkDir = null,
        string? workerContainerId = null,
        Func<string, string?>? secretProvider = null,
        Func<string, string?, Task<ArtifactRef>>? artifactSaver = null,
        Action<string, string>? onOutput = null)
    {
        _client = DockerClientFactory.Create();
        _workDir = workDir ?? Directory.GetCurrentDirectory();
        _hostWorkDir = hostWorkDir ?? _workDir;
        _workerContainerId = workerContainerId;
        _secretProvider = secretProvider ?? (name => Environment.GetEnvironmentVariable(name));
        _artifactSaver = artifactSaver;
        _onOutput = onOutput;
        _userSpec = LinuxInterop.GetUserSpec();
        _dockerSocketGid = LinuxInterop.GetDockerSocketGid();
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    /// <summary>
    /// Write informational message to both console and log output callback.
    /// </summary>
    private void WriteInfo(string message)
    {
        Console.WriteLine(message);
        _onOutput?.Invoke("m", message + "\n");
    }

    public async Task<bool> RunAsync(IReadOnlyList<Step<object?>> steps, CancellationToken cancellationToken = default)
    {
        var branch = await GetGitBranch();
        var commit = await GetGitCommit();
        var networkName = $"ilmarinen-{Guid.NewGuid():N}";
        var runId = Guid.CreateVersion7();

        // Set up artifact saver (use custom if provided, otherwise save to local filesystem)
        var artifactSaver = _artifactSaver ?? CreateLocalArtifactSaver(runId);

        WriteInfo($"Running {steps.Count} step(s)...");
        WriteInfo($"Branch: {branch}, Commit: {commit[..Math.Min(8, commit.Length)]}");
        WriteInfo("");

        // Start the agent API server (starts automatically in constructor)
        await using var apiServer = new AgentApiServer();
        WriteInfo($"Agent API: http://localhost:{apiServer.Port}");
        WriteInfo("");

        // Create a network for this pipeline run
        await CreateNetworkAsync(networkName);

        try
        {
            foreach (var step in steps)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Resolve image lazily (may depend on previous step outputs)
                var image = step.ImageResolver();

                WriteInfo($"=== Step: {step.Name} ===");
                WriteInfo($"Image: {image}");

                try
                {
                    await RunStepAsync(step, image, branch, commit, networkName, apiServer, artifactSaver, cancellationToken);
                    WriteInfo($"=== {step.Name}: SUCCESS ===");
                    WriteInfo("");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    WriteInfo($"=== {step.Name}: CANCELLED ===");
                    throw;
                }
                catch (Exception ex)
                {
                    WriteInfo($"=== {step.Name}: FAILED ===");
                    WriteInfo($"Error: {ex.Message}");
                    return false;
                }
            }

            WriteInfo("All steps completed successfully!");
            return true;
        }
        finally
        {
            // Clean up the network
            await RemoveNetworkAsync(networkName);
        }
    }

    private const string ShellScript = """
        #!/bin/sh
        set -e
        API="${ILMARINEN_API}"
        TOKEN="${ILMARINEN_TOKEN}"

        # Detect HTTP client
        if command -v curl >/dev/null 2>&1; then
            # Prefer curl for streaming (-N disables buffering)
            http_get() { curl -s -H "Authorization: Bearer $TOKEN" "$1"; }
            http_post() { curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$2" "$1"; }
            http_stream() {
                curl -sN -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$2" "$1" | stream_output
                return $?
            }
        elif command -v wget >/dev/null 2>&1; then
            http_get() {
                _rc=0; _body=$(wget -qO- --header="Authorization: Bearer $TOKEN" "$1" 2>/dev/null) || _rc=$?
                if [ -z "$_body" ] && [ "$_rc" -ne 0 ]; then return "$_rc"; fi
                printf '%s' "$_body"
            }
            http_post() {
                _rc=0; _body=$(wget -qO- --header="Authorization: Bearer $TOKEN" --header="Content-Type: application/json" --post-data="$2" "$1" 2>/dev/null) || _rc=$?
                if [ -z "$_body" ] && [ "$_rc" -ne 0 ]; then return "$_rc"; fi
                printf '%s' "$_body"
            }
            http_stream() {
                wget -qO- --header="Authorization: Bearer $TOKEN" --header="Content-Type: application/json" --post-data="$2" "$1" 2>/dev/null | stream_output
                return $?
            }
        else
            echo "Error: Neither wget nor curl found" >&2
            exit 1
        fi

        # Extract JSON string value: json_str '{"k":"v"}' 'k' -> v
        json_str() { echo "$1" | sed -n 's/.*"'"$2"'"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1 | sed 's/\\n/\n/g'; }
        # Extract JSON number value: json_num '{"k":123}' 'k' -> 123
        json_num() { echo "$1" | sed -n 's/.*"'"$2"'"[[:space:]]*:[[:space:]]*\([0-9-]*\).*/\1/p' | head -1; }
        # Check if JSON has a field
        json_has() { echo "$1" | grep -q "\"$2\""; }

        # Unescape JSON string (handles \n, \t, \", \\)
        json_unescape() {
            # Use printf %b to interpret escape sequences
            printf '%b' "$(printf '%s' "$1" | sed 's/\\"/"/g')"
        }

        # Stream NDJSON output - parses {"t":"o/e/x","d":"...","c":N} lines
        # Returns exit code from the "x" message
        stream_output() {
            _stream_exit=0
            _got_exit=false
            while IFS= read -r line || [ -n "$line" ]; do
                [ -z "$line" ] && continue
                # Extract type field
                _type=$(echo "$line" | sed -n 's/.*"t":"\([^"]*\)".*/\1/p')
                case "$_type" in
                    o)
                        # stdout chunk
                        _data=$(echo "$line" | sed -n 's/.*"d":"\([^"]*\)".*/\1/p')
                        printf '%s' "$(json_unescape "$_data")"
                        ;;
                    e)
                        # stderr chunk
                        _data=$(echo "$line" | sed -n 's/.*"d":"\([^"]*\)".*/\1/p')
                        printf '%s' "$(json_unescape "$_data")" >&2
                        ;;
                    x)
                        # exit message
                        _got_exit=true
                        _stream_exit=$(echo "$line" | sed -n 's/.*"c":\([0-9-]*\).*/\1/p')
                        _stream_exit=${_stream_exit:-0}
                        # Check for error
                        if echo "$line" | grep -q '"error"'; then
                            _error=$(echo "$line" | sed -n 's/.*"error":"\([^"]*\)".*/\1/p')
                            _extype=$(echo "$line" | sed -n 's/.*"exceptionType":"\([^"]*\)".*/\1/p')
                            _depth=$(echo "$line" | sed -n 's/.*"nestingDepth":\([0-9]*\).*/\1/p')
                            echo "=== ilmarinen error ===" >&2
                            [ -n "$_extype" ] && echo "Type: $_extype" >&2
                            [ -n "$_depth" ] && [ "$_depth" -gt 1 ] 2>/dev/null && echo "Nesting depth: $_depth" >&2
                            echo "Error: $_error" >&2
                            echo "=======================" >&2
                        fi
                        ;;
                esac
            done
            if [ "$_got_exit" = false ]; then
                echo "Error: Lost connection to API server" >&2
                return 1
            fi
            return $_stream_exit
        }

        # Handle structured error responses
        handle_error() {
            local result="$1"
            local default_exit="${2:-1}"

            if json_has "$result" "error"; then
                local error_msg=$(json_str "$result" "error")
                local exit_code=$(json_num "$result" "exitCode")
                local stderr_out=$(json_str "$result" "stderr")
                local exception_type=$(json_str "$result" "exceptionType")
                local nesting_depth=$(json_num "$result" "nestingDepth")

                echo "=== ilmarinen error ===" >&2
                [ -n "$exception_type" ] && echo "Type: $exception_type" >&2
                [ -n "$nesting_depth" ] && [ "$nesting_depth" -gt 1 ] 2>/dev/null && echo "Nesting depth: $nesting_depth" >&2
                echo "Error: $error_msg" >&2
                if [ -n "$stderr_out" ]; then
                    echo "--- stderr ---" >&2
                    printf '%s\n' "$stderr_out" >&2
                    echo "--- end stderr ---" >&2
                fi
                echo "=======================" >&2

                [ -n "$exit_code" ] && [ "$exit_code" != "0" ] && return "$exit_code"
                return "$default_exit"
            fi
            return 0
        }

        case "$1" in
            --help|-h|help)
                echo "ilmarinen-agent - In-container agent CLI"
                echo ""
                echo "Usage: ilmarinen-agent <command> [options]"
                echo ""
                echo "Commands:"
                echo "  build [-f <dockerfile>] [-t <tag>] [--build-arg KEY=VALUE]... [<context>]"
                echo "                                 Build a container image"
                echo "  run <image> [-- <command>...]  Run a container"
                echo "  info <branch|commit>           Get build info"
                echo "  secret get <name>              Get a secret value"
                echo "  --version                      Show version"
                exit 0
                ;;
            --version|-v)
                echo "ilmarinen-agent 0.1.0"
                exit 0
                ;;
            info)
                shift
                [ -z "$1" ] && { echo "Usage: ilmarinen-agent info <branch|commit>" >&2; exit 1; }
                result=$(http_get "${API}/api/info/$1") || { echo "Error: Cannot reach API server" >&2; exit 1; }
                handle_error "$result" || exit $?
                json_str "$result" "value"
                ;;
            secret)
                shift
                [ "$1" != "get" ] || [ -z "$2" ] && { echo "Usage: ilmarinen-agent secret get <name>" >&2; exit 1; }
                result=$(http_get "${API}/api/secret/$2") || { echo "Error: Cannot reach API server" >&2; exit 1; }
                handle_error "$result" || exit $?
                printf '%s' "$(json_str "$result" "value")"
                ;;
            run)
                shift
                [ -z "$1" ] && { echo "Usage: ilmarinen-agent run <image> [-- <command>...]" >&2; exit 1; }
                image="$1"; shift
                [ "$1" = "--" ] && shift
                # Build command JSON array
                cmd="["; first=1
                for arg in "$@"; do
                    [ $first -eq 1 ] && first=0 || cmd="$cmd,"
                    escaped=$(printf '%s' "$arg" | sed 's/"/\\"/g')
                    cmd="$cmd\"$escaped\""
                done
                cmd="$cmd]"
                # Use streaming for real-time output
                http_stream "${API}/api/run" "{\"image\":\"$image\",\"command\":$cmd}"
                exit $?
                ;;
            build)
                shift
                dockerfile=""
                tag=""
                context=""
                build_args=""

                while [ $# -gt 0 ]; do
                    case "$1" in
                        -f|--file)
                            [ -z "$2" ] && { echo "Error: -f requires a dockerfile path" >&2; exit 1; }
                            dockerfile="$2"
                            shift 2
                            ;;
                        -t|--tag)
                            [ -z "$2" ] && { echo "Error: -t requires a tag" >&2; exit 1; }
                            tag="$2"
                            shift 2
                            ;;
                        --build-arg)
                            [ -z "$2" ] && { echo "Error: --build-arg requires KEY=VALUE" >&2; exit 1; }
                            # Parse KEY=VALUE
                            arg_key="${2%%=*}"
                            arg_value="${2#*=}"
                            escaped_key=$(printf '%s' "$arg_key" | sed 's/"/\\"/g')
                            escaped_value=$(printf '%s' "$arg_value" | sed 's/"/\\"/g')
                            [ -n "$build_args" ] && build_args="$build_args,"
                            build_args="$build_args\"$escaped_key\":\"$escaped_value\""
                            shift 2
                            ;;
                        -*)
                            echo "Error: Unknown option: $1" >&2
                            echo "Usage: ilmarinen-agent build [-f <dockerfile>] [-t <tag>] [--build-arg KEY=VALUE]... [<context>]" >&2
                            exit 1
                            ;;
                        *)
                            [ -n "$context" ] && { echo "Error: Multiple context directories specified" >&2; exit 1; }
                            context="$1"
                            shift
                            ;;
                    esac
                done

                [ -z "$dockerfile" ] && dockerfile="Dockerfile"
                [ -z "$context" ] && context="."

                json_dockerfile=$(printf '%s' "$dockerfile" | sed 's/"/\\"/g')
                json_context=$(printf '%s' "$context" | sed 's/"/\\"/g')

                payload="{\"dockerfile\":\"$json_dockerfile\",\"context\":\"$json_context\""
                [ -n "$tag" ] && payload="$payload,\"tag\":\"$(printf '%s' "$tag" | sed 's/"/\\"/g')\""
                [ -n "$build_args" ] && payload="$payload,\"buildArgs\":{$build_args}"
                payload="$payload}"

                result=$(http_post "${API}/api/build" "$payload") || { echo "Error: Cannot reach API server" >&2; exit 1; }

                if json_has "$result" "error"; then
                    handle_error "$result"
                    exit $?
                fi

                json_str "$result" "reference"
                ;;
            *)
                echo "Unknown command: $1" >&2
                echo "Run 'ilmarinen-agent --help' for usage." >&2
                exit 1
                ;;
        esac
        """;

    /// <summary>
    /// Creates a tar archive containing a single executable script.
    /// </summary>
    /// <remarks>
    /// Why a tar copy instead of bind mounting the script?
    ///
    /// In Docker-in-Docker scenarios (e.g., workers running inside containers), bind mounts
    /// reference paths on the Docker *host*, not the intermediate container. If we write
    /// the script to /tmp/foo.sh inside a worker container and try to bind-mount it into
    /// a job container, Docker looks for /tmp/foo.sh on the actual host where it doesn't
    /// exist, causing "Permission denied" errors.
    ///
    /// By using ExtractArchiveToContainerAsync, we copy the script directly into the
    /// container's filesystem, bypassing path mapping issues entirely.
    /// </remarks>
    private static MemoryStream CreateTarWithScript(string scriptContent, string fileName)
    {
        var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, TarEntryFormat.Ustar, leaveOpen: true))
        {
            var entry = new UstarTarEntry(TarEntryType.RegularFile, fileName)
            {
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                       UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                       UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
                DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(scriptContent))
            };
            writer.WriteEntry(entry);
        }

        stream.Position = 0;
        return stream;
    }

    private Func<string, string?, Task<ArtifactRef>> CreateLocalArtifactSaver(Guid runId)
    {
        var artifactDir = Path.Combine(_workDir, ".ilmarinen", "artifacts", runId.ToString());

        return async (hostPath, name) =>
        {
            var artifactId = Guid.CreateVersion7();
            var fileName = name ?? Path.GetFileName(hostPath);
            var safeFileName = SanitizeFileName(fileName);
            var destFileName = $"{artifactId}-{safeFileName}";
            var destPath = Path.Combine(artifactDir, destFileName);

            Directory.CreateDirectory(artifactDir);

            await using var source = File.OpenRead(hostPath);
            await using var dest = File.Create(destPath);
            await source.CopyToAsync(dest);

            var fileInfo = new FileInfo(destPath);

            WriteInfo($"Artifact saved: {fileName} ({fileInfo.Length:N0} bytes)");
            WriteInfo($"  -> {destPath}");

            return new ArtifactRef
            {
                Name = fileName,
                Size = fileInfo.Length
            };
        };
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }

    private async Task CreateNetworkAsync(string name)
    {
        await _client.Networks.CreateNetworkAsync(new NetworksCreateParameters
        {
            Name = name,
            Driver = "bridge",
            Labels = new Dictionary<string, string>
            {
                ["ilmarinen.test.pid"] = Environment.ProcessId.ToString()
            }
        });

        // In Docker-in-Docker: connect worker container to the network so job containers can reach the AgentApiServer via Docker DNS
        if (_workerContainerId != null)
        {
            await _client.Networks.ConnectNetworkAsync(name, new NetworkConnectParameters
            {
                Container = _workerContainerId
            });
        }
    }

    private async Task RemoveNetworkAsync(string name)
    {
        // In Docker-in-Docker: disconnect worker container first
        if (_workerContainerId != null)
        {
            try
            {
                await _client.Networks.DisconnectNetworkAsync(name, new NetworkDisconnectParameters
                {
                    Container = _workerContainerId
                });
            }
            catch (Exception ex)
            {
                WriteInfo($"Warning: failed to disconnect worker from network {name}: {ex.Message}");
            }
        }

        try
        {
            await _client.Networks.DeleteNetworkAsync(name);
        }
        catch (Exception ex)
        {
            WriteInfo($"Warning: failed to remove network {name}: {ex.Message}");
        }
    }

    private async Task RunStepAsync(Step<object?> step, ImageRef image, string branch, string commit,
        string networkName, AgentApiServer apiServer, Func<string, string?, Task<ArtifactRef>> artifactSaver,
        CancellationToken cancellationToken = default)
    {
        // Pull the image if needed
        await PullImageIfNeeded(image.Reference);

        WriteInfo("Agent CLI: shell script");

        // Create and start the container
        var containerId = await CreateContainerAsync(image.Reference, networkName, apiServer);

        try
        {
            // Copy agent script into container (avoids bind mount path issues in Docker-in-Docker)
            using (var tarStream = CreateTarWithScript(ShellScript, "ilmarinen-agent"))
            {
                await _client.Containers.ExtractArchiveToContainerAsync(
                    containerId,
                    new ContainerPathStatParameters { Path = "/usr/local/bin", AllowOverwriteDirWithFile = false },
                    tarStream);
            }

            await _client.Containers.StartContainerAsync(containerId, new ContainerStartParameters());

            var context = new DockerJobContext(
                _client,
                containerId,
                "/workspace",
                _hostWorkDir,
                networkName,
                branch,
                commit,
                _secretProvider,
                artifactSaver,
                _onOutput,
                _userSpec,
                _dockerSocketGid);

            // Set the context on the API server so CLI requests are routed correctly
            apiServer.SetContext(context);

            try
            {
                // Execute the action and capture output. When cancelled, we stop the container which kills all processes inside it.
                using var reg = cancellationToken.Register(() =>
                {
                    // Fire-and-forget: stopping the container kills the entire process tree
                    _ = _client.Containers.StopContainerAsync(containerId, new ContainerStopParameters
                    {
                        WaitBeforeKillSeconds = 5
                    });
                });

                await step.Action(context);
            }
            finally
            {
                await context.CleanupAsync();
            }
        }
        finally
        {
            // Stop and remove container. Best-effort: a failure here means a leaked container, so leave a breadcrumb.
            try
            {
                await _client.Containers.StopContainerAsync(containerId, new ContainerStopParameters());
            }
            catch (Exception ex)
            {
                WriteInfo($"Warning: failed to stop container {containerId[..12]}: {ex.Message}");
            }

            try
            {
                await _client.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true });
            }
            catch (Exception ex)
            {
                WriteInfo($"Warning: failed to remove container {containerId[..12]}: {ex.Message}");
            }
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
            WriteInfo($"Pulling image: {image}");
            await _client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = image },
                null,
                new Progress<JSONMessage>(m =>
                {
                    if (!string.IsNullOrEmpty(m.Status))
                        WriteInfo($"  {m.Status}");
                }));
        }
    }

    private async Task<string> CreateContainerAsync(string image, string networkName,
        AgentApiServer apiServer)
    {
        var binds = new List<string>
        {
            $"{_hostWorkDir}:/workspace",  // Use host path for Docker bind mounts
            "/var/run/docker.sock:/var/run/docker.sock" // For nested containers
        };

        // In Docker-in-Docker: use the worker container ID as the API host (Docker DNS resolves it); otherwise use host.docker.internal to reach the host machine
        var apiHost = _workerContainerId ?? "host.docker.internal";

        var env = new List<string>
        {
            $"ILMARINEN_API=http://{apiHost}:{apiServer.Port}",
            $"ILMARINEN_TOKEN={apiServer.Token}",
            "HOME=/tmp",                  // Writable home dir when running as non-root (no passwd entry)
            "DOCKER_CONFIG=/tmp/.docker"  // Docker CLI config dir when running as non-root
        };

        var response = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = image,
            Tty = true,
            AttachStdout = true,
            AttachStderr = true,
            WorkingDir = "/workspace",
            Cmd = ["/bin/sh", "-c", "tail -f /dev/null"], // Keep container running
            Env = env,
            User = _userSpec,
            HostConfig = new HostConfig
            {
                Binds = binds,
                NetworkMode = networkName,
                AutoRemove = false,
                ExtraHosts = ["host.docker.internal:host-gateway"],  // Enable host.docker.internal on Linux
                GroupAdd = _dockerSocketGid.HasValue ? [_dockerSocketGid.Value.ToString()] : null
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
