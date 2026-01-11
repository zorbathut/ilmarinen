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
        var networkName = $"ilmarinen-{Guid.NewGuid():N}";

        Console.WriteLine($"Running {steps.Count} step(s)...");
        Console.WriteLine($"Branch: {branch}, Commit: {commit[..Math.Min(8, commit.Length)]}");
        Console.WriteLine();

        // Start the agent API server (starts automatically in constructor)
        await using var apiServer = new AgentApiServer();
        Console.WriteLine($"Agent API: http://localhost:{apiServer.Port}");
        Console.WriteLine();

        // Create a network for this pipeline run
        await CreateNetworkAsync(networkName);

        try
        {
            foreach (var step in steps)
            {
                Console.WriteLine($"=== Step: {step.Name} ===");
                Console.WriteLine($"Image: {step.Image}");

                try
                {
                    await RunStepAsync(step, branch, commit, networkName, apiServer);
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
        finally
        {
            // Clean up the network
            await RemoveNetworkAsync(networkName);
        }
    }

    private static string? _shellScriptPath;

    private static string GetOrCreateShellScript()
    {
        if (_shellScriptPath != null && File.Exists(_shellScriptPath))
            return _shellScriptPath;

        _shellScriptPath = Path.Combine(Path.GetTempPath(), $"ilmarinen-cli-{Guid.NewGuid():N}.sh");
        File.WriteAllText(_shellScriptPath, ShellScript);
        // Make executable (no-op on Windows, works on Unix)
        try { File.SetUnixFileMode(_shellScriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute); } catch { }
        return _shellScriptPath;
    }

    private const string ShellScript = """
        #!/bin/sh
        set -e
        API="${ILMARINEN_API}"
        TOKEN="${ILMARINEN_TOKEN}"

        # Detect HTTP client
        if command -v curl >/dev/null 2>&1; then
            # Prefer curl for streaming (-N disables buffering)
            http_get() { curl -sf -H "Authorization: Bearer $TOKEN" "$1"; }
            http_post() { curl -sf -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$2" "$1"; }
            http_stream() {
                curl -sfN -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$2" "$1" | stream_output
                return $?
            }
        elif command -v wget >/dev/null 2>&1; then
            http_get() { wget -qO- --header="Authorization: Bearer $TOKEN" "$1" 2>/dev/null; }
            http_post() { wget -qO- --header="Authorization: Bearer $TOKEN" --header="Content-Type: application/json" --post-data="$2" "$1" 2>/dev/null; }
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
                echo "ilmarinen - Container orchestration CLI"
                echo ""
                echo "Usage: ilmarinen <command> [options]"
                echo ""
                echo "Commands:"
                echo "  build [-f <dockerfile>] [-t <tag>] [<context>]"
                echo "                                 Build a container image"
                echo "  run <image> [-- <command>...]  Run a container"
                echo "  info <branch|commit>           Get build info"
                echo "  secret get <name>              Get a secret value"
                echo "  --version                      Show version"
                exit 0
                ;;
            --version|-v)
                echo "ilmarinen 0.1.0"
                exit 0
                ;;
            info)
                shift
                [ -z "$1" ] && { echo "Usage: ilmarinen info <branch|commit>" >&2; exit 1; }
                result=$(http_get "${API}/api/info/$1")
                handle_error "$result" || exit $?
                json_str "$result" "value"
                ;;
            secret)
                shift
                [ "$1" != "get" ] || [ -z "$2" ] && { echo "Usage: ilmarinen secret get <name>" >&2; exit 1; }
                result=$(http_get "${API}/api/secret/$2")
                handle_error "$result" || exit $?
                printf '%s' "$(json_str "$result" "value")"
                ;;
            run)
                shift
                [ -z "$1" ] && { echo "Usage: ilmarinen run <image> [-- <command>...]" >&2; exit 1; }
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
                        -*)
                            echo "Error: Unknown option: $1" >&2
                            echo "Usage: ilmarinen build [-f <dockerfile>] [-t <tag>] [<context>]" >&2
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

                if [ -n "$tag" ]; then
                    json_tag=$(printf '%s' "$tag" | sed 's/"/\\"/g')
                    payload="{\"dockerfile\":\"$json_dockerfile\",\"tag\":\"$json_tag\",\"context\":\"$json_context\"}"
                else
                    payload="{\"dockerfile\":\"$json_dockerfile\",\"context\":\"$json_context\"}"
                fi

                result=$(http_post "${API}/api/build" "$payload")

                if json_has "$result" "error"; then
                    handle_error "$result"
                    exit $?
                fi

                json_str "$result" "reference"
                ;;
            *)
                echo "Unknown command: $1" >&2
                echo "Run 'ilmarinen --help' for usage." >&2
                exit 1
                ;;
        esac
        """;

    private async Task CreateNetworkAsync(string name)
    {
        await _client.Networks.CreateNetworkAsync(new NetworksCreateParameters
        {
            Name = name,
            Driver = "bridge"
        });
    }

    private async Task RemoveNetworkAsync(string name)
    {
        try
        {
            await _client.Networks.DeleteNetworkAsync(name);
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private async Task RunStepAsync(Step step, string branch, string commit, string networkName,
        AgentApiServer apiServer)
    {
        // Pull the image if needed
        await PullImageIfNeeded(step.Image);

        Console.WriteLine("Agent CLI: shell script");

        // Create and start the container
        var containerId = await CreateContainerAsync(step.Image, networkName, apiServer);

        try
        {
            await _client.Containers.StartContainerAsync(containerId, new ContainerStartParameters());

            var context = new DockerJobContext(
                _client,
                containerId,
                "/workspace",
                _workDir,
                networkName,
                branch,
                commit,
                _secretProvider);

            // Set the context on the API server so CLI requests are routed correctly
            apiServer.SetContext(context);

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
            try
            {
                await _client.Containers.StopContainerAsync(containerId, new ContainerStopParameters());
            }
            catch { }

            try
            {
                await _client.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true });
            }
            catch { }
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

    private async Task<string> CreateContainerAsync(string image, string networkName,
        AgentApiServer apiServer)
    {
        var shellScriptPath = GetOrCreateShellScript();

        var binds = new List<string>
        {
            $"{_workDir}:/workspace",
            "/var/run/docker.sock:/var/run/docker.sock", // For nested containers
            $"{shellScriptPath}:/usr/local/bin/ilmarinen:ro" // CLI shell script
        };

        var env = new List<string>
        {
            $"ILMARINEN_API=http://host.docker.internal:{apiServer.Port}",
            $"ILMARINEN_TOKEN={apiServer.Token}"
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
            HostConfig = new HostConfig
            {
                Binds = binds,
                NetworkMode = networkName,
                AutoRemove = false,
                ExtraHosts = ["host.docker.internal:host-gateway"]  // Enable host.docker.internal on Linux
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
