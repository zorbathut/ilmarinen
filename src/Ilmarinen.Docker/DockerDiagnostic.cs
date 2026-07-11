using Docker.DotNet.Models;
using Docker.DotNet;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.Docker;

/// <summary>
/// Runs a sequence of checks that exercise the capabilities a worker needs to actually
/// run jobs: Docker daemon connectivity, image pull, container exec, output capture,
/// and agent API reachability (the most common Docker-in-Docker failure mode).
///
/// The diagnostic is custom code rather than a wrapped pipeline run because each step
/// knows what it's testing and can attach a specific FailureKind + Suggestion to its
/// failure — far more actionable for operators than parsing exception strings out of
/// a generic pipeline failure.
/// </summary>
public class DockerDiagnostic : IAsyncDisposable
{
    private const string TestImage = "alpine:latest";

    private readonly DockerClient _client;
    private readonly string? _workerContainerId;
    private readonly string? _userSpec;
    private readonly uint? _dockerSocketGid;

    private readonly string _expectedToken = $"ilmarinen-diagnostic-{Guid.NewGuid():N}";
    private string? _testContainerId;
    private string? _testNetworkName;
    private AgentApiServer? _apiServer;
    private bool _disposed;

    public DockerDiagnostic(string? workerContainerId)
        : this(DockerClientFactory.Create(), workerContainerId) { }

    /// <summary>For tests that need to point at a specific Docker URI without mutating DOCKER_HOST.</summary>
    internal DockerDiagnostic(string dockerHostUri, string? workerContainerId)
        : this(new DockerClientConfiguration(new Uri(dockerHostUri)).CreateClient(), workerContainerId) { }

    private DockerDiagnostic(DockerClient client, string? workerContainerId)
    {
        _client = client;
        _workerContainerId = workerContainerId;
        _userSpec = LinuxInterop.GetUserSpec();
        _dockerSocketGid = LinuxInterop.GetDockerSocketGid();
    }

    public async Task<DiagnosticReport> RunAsync(IProgress<DiagnosticStepResult>? progress, CancellationToken ct)
    {
        var checkedAt = DateTime.UtcNow;
        var steps = new List<DiagnosticStepResult>();
        string? failedStepName = null;
        string? failedMessage = null;

        async Task RunStep(string name, Func<CancellationToken, Task<DiagnosticStepResult>> fn, bool isFatal)
        {
            // Skip subsequent fatal steps after a fatal failure, but always run cleanup.
            if (failedStepName != null && isFatal) return;

            var sw = Stopwatch.StartNew();
            DiagnosticStepResult result;
            try
            {
                ct.ThrowIfCancellationRequested();
                result = await fn(ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = Fail(ex.Message, FailureKind.Unknown, null);
            }

            result = result with { Name = name, Duration = sw.Elapsed };
            steps.Add(result);
            progress?.Report(result);

            // Any step failure flips overall to Unhealthy. Non-fatal steps don't abort the run (so cleanup still happens after a docker_daemon failure), but their failure is not silently masked — a leaked container is a real operator problem.
            if (!result.Success && failedStepName == null)
            {
                failedStepName = name;
                failedMessage = result.Message;
            }
        }

        await RunStep("docker_daemon", CheckDaemonAsync, isFatal: true);
        await RunStep("dns_worker", CheckWorkerDnsAsync, isFatal: true);
        await RunStep("image_pull", CheckImagePullAsync, isFatal: true);
        await RunStep("container_run", CheckContainerRunAsync, isFatal: true);
        await RunStep("output_capture", CheckOutputCaptureAsync, isFatal: true);
        await RunStep("dns_container", CheckContainerDnsAsync, isFatal: true);
        await RunStep("agent_api_reachability", CheckAgentApiReachabilityAsync, isFatal: true);
        await RunStep("cleanup", CleanupAsync, isFatal: false);

        return new DiagnosticReport
        {
            Status = failedStepName != null ? DiagnosticStatus.Unhealthy : DiagnosticStatus.Healthy,
            Summary = failedStepName != null
                ? $"{failedStepName} failed: {failedMessage ?? "unknown error"}"
                : "All checks passed.",
            Steps = steps,
            CheckedAt = checkedAt
        };
    }

    private async Task<DiagnosticStepResult> CheckDaemonAsync(CancellationToken ct)
    {
        try
        {
            var info = await _client.System.GetSystemInfoAsync(ct);
            return Ok($"Daemon reachable (version {info.ServerVersion})");
        }
        catch (Exception ex)
        {
            var msg = ex.Message.ToLowerInvariant();
            if (msg.Contains("permission denied"))
            {
                return Fail($"Permission denied talking to Docker: {ex.Message}",
                    FailureKind.SocketPermission,
                    "The current user can't access the Docker socket. On Linux, add the user to the docker group: `sudo usermod -aG docker $USER`, then log out and back in.");
            }
            if (msg.Contains("no such file") || msg.Contains("connection refused")
                || msg.Contains("cannot connect") || msg.Contains("actively refused"))
            {
                return Fail($"Cannot reach Docker daemon: {ex.Message}",
                    FailureKind.SocketUnreachable,
                    "Docker daemon is not reachable. Is Docker running? Check `systemctl status docker` (Linux) or that Docker Desktop is started.");
            }
            return Fail($"Docker daemon error: {ex.Message}", FailureKind.DaemonError, null);
        }
    }

    private async Task<DiagnosticStepResult> CheckImagePullAsync(CancellationToken ct)
    {
        try
        {
            // Always call CreateImageAsync, even if the image looks cached locally. Docker still contacts the registry to verify the manifest is up-to-date — that's exactly the round-trip we want to test. If layers are unchanged, no bytes are downloaded; the check is fast (~hundreds of ms) but real.
            await _client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = TestImage },
                null,
                new Progress<JSONMessage>(),
                ct);
            return Ok($"Registry reachable for {TestImage}");
        }
        catch (Exception ex)
        {
            var msg = ex.Message.ToLowerInvariant();
            if (msg.Contains("no such host") || msg.Contains("dial tcp") || msg.Contains("dns"))
            {
                return Fail($"Cannot pull {TestImage}: {ex.Message}",
                    FailureKind.RegistryUnreachable,
                    "DNS or network failure reaching the registry. Check the worker host's internet connectivity and DNS resolution.");
            }
            if (msg.Contains("timeout") || msg.Contains("timed out"))
            {
                return Fail($"Pull timed out: {ex.Message}",
                    FailureKind.Timeout,
                    "Registry connection timed out. Check network connectivity or proxy configuration.");
            }
            return Fail($"Pull failed: {ex.Message}", FailureKind.RegistryUnreachable, null);
        }
    }

    private async Task<DiagnosticStepResult> CheckContainerRunAsync(CancellationToken ct)
    {
        try
        {
            // Spin up an AgentApiServer so the agent_api_reachability step can probe it. Doing this here (rather than as a separate step) means the test container is already configured with the right ILMARINEN_API/_TOKEN env vars from start.
            _apiServer = new AgentApiServer();

            _testNetworkName = $"ilmarinen-diag-{Guid.NewGuid():N}";
            await _client.Networks.CreateNetworkAsync(new NetworksCreateParameters
            {
                Name = _testNetworkName,
                Driver = "bridge",
                Labels = new Dictionary<string, string>
                {
                    ["ilmarinen.diagnostic.pid"] = Environment.ProcessId.ToString()
                }
            }, ct);

            // In Docker-in-Docker, attach the worker container to the diagnostic network so the test container can reach the AgentApiServer running inside the worker.
            if (_workerContainerId != null)
            {
                await _client.Networks.ConnectNetworkAsync(_testNetworkName, new NetworkConnectParameters
                {
                    Container = _workerContainerId
                }, ct);
            }

            var apiHost = _workerContainerId ?? "host.docker.internal";

            var response = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = TestImage,
                Tty = true,
                AttachStdout = true,
                AttachStderr = true,
                Cmd = ["/bin/sh", "-c", "tail -f /dev/null"],
                Env =
                [
                    $"ILMARINEN_API=http://{apiHost}:{_apiServer.Port}",
                    $"ILMARINEN_TOKEN={_apiServer.Token}"
                ],
                User = _userSpec,
                HostConfig = new HostConfig
                {
                    NetworkMode = _testNetworkName,
                    AutoRemove = false,
                    ExtraHosts = ["host.docker.internal:host-gateway"],
                    GroupAdd = _dockerSocketGid.HasValue ? [_dockerSocketGid.Value.ToString()] : null
                }
            }, ct);
            _testContainerId = response.ID;

            await _client.Containers.StartContainerAsync(_testContainerId, new ContainerStartParameters(), ct);

            return Ok($"Container started on network {_testNetworkName}");
        }
        catch (Exception ex)
        {
            return Fail($"Could not create or start container: {ex.Message}",
                FailureKind.ContainerCreateFailed,
                "Docker accepted the daemon connection but could not run a container. Check daemon logs (`journalctl -u docker` on Linux) for cgroup, networking, or storage-driver errors.");
        }
    }

    private async Task<DiagnosticStepResult> CheckOutputCaptureAsync(CancellationToken ct)
    {
        if (_testContainerId == null)
            return Fail("No test container available", FailureKind.Unknown, null);

        try
        {
            var (stdout, stderr, exitCode) = await ExecAsync(
                _testContainerId, ["sh", "-c", $"echo {_expectedToken}"]);

            if (exitCode != 0)
            {
                return Fail($"echo exited with code {exitCode}: stderr='{Truncate(stderr, 200)}'",
                    FailureKind.ContainerExecFailed, null);
            }

            if (!stdout.Contains(_expectedToken))
            {
                return Fail($"Expected '{_expectedToken}' in stdout, got '{Truncate(stdout, 200)}'",
                    FailureKind.OutputMismatch,
                    "The container ran but its output didn't match. Likely a Docker logging driver misconfiguration.");
            }

            return Ok("Container output captured correctly");
        }
        catch (Exception ex)
        {
            return Fail($"Exec failed: {ex.Message}", FailureKind.ContainerExecFailed, null);
        }
    }

    private async Task<DiagnosticStepResult> CheckWorkerDnsAsync(CancellationToken ct)
    {
        // Resolve from the worker host itself via the OS resolver. Pairs with dns_container: if dns_worker fails, host DNS is broken (resolv.conf, network); if dns_worker passes but dns_container fails, the failure is specifically Docker's embedded resolver forwarding (127.0.0.11), not the host.
        try
        {
            var entry = await System.Net.Dns.GetHostEntryAsync("docker.io", ct);
            var addr = entry.AddressList.Length > 0 ? entry.AddressList[0].ToString() : "(no address)";
            return Ok($"Worker resolved docker.io to {addr}");
        }
        catch (Exception ex)
        {
            return Fail($"Worker could not resolve docker.io: {ex.Message}",
                FailureKind.WorkerDnsFailure,
                "The worker host itself cannot resolve DNS. Check /etc/resolv.conf and that the configured nameservers are reachable. Image pulls will also fail until this is fixed.");
        }
    }

    private async Task<DiagnosticStepResult> CheckContainerDnsAsync(CancellationToken ct)
    {
        if (_testContainerId == null)
            return Fail("No test container available", FailureKind.Unknown, null);

        try
        {
            // Tests Docker's embedded DNS resolver (127.0.0.11) — the path real pipelines use during `docker build` metadata fetch and any RUN step that hits the network. Distinct from the host resolver tested by dns_worker.
            // busybox provides nslookup in alpine; exit 0 on resolve, non-zero on failure.
            var (stdout, stderr, exitCode) = await ExecAsync(
                _testContainerId, ["nslookup", "docker.io"]);

            if (exitCode != 0)
            {
                return Fail(
                    $"nslookup docker.io failed (exit {exitCode}): {Truncate(stderr.Length > 0 ? stderr : stdout, 200)}",
                    FailureKind.ContainerDnsFailure,
                    "DNS works on the host (dns_worker passed) but not inside containers. Docker's embedded resolver at 127.0.0.11 is failing to forward queries. Usually a stale daemon — try restarting Docker.");
            }

            return Ok("Container resolved docker.io");
        }
        catch (Exception ex)
        {
            return Fail($"DNS check failed: {ex.Message}", FailureKind.ContainerDnsFailure, null);
        }
    }

    private async Task<DiagnosticStepResult> CheckAgentApiReachabilityAsync(CancellationToken ct)
    {
        if (_testContainerId == null || _apiServer == null)
            return Fail("Test container or API server not initialized", FailureKind.Unknown, null);

        try
        {
            // busybox wget (alpine default): -q quiet, -O- to stdout. Exit 0 on HTTP 2xx. Token-in-shell is safe today because AgentApiServer's token is Guid.ToString("N") — pure [0-9a-f], no shell-special characters. If that ever changes, this command will need to escape (or, better, pass the token via a here-doc).
            var (stdout, stderr, exitCode) = await ExecAsync(_testContainerId,
            [
                "sh", "-c",
                "wget -qO- --header=\"Authorization: Bearer $ILMARINEN_TOKEN\" \"$ILMARINEN_API/api/ping\""
            ]);

            if (exitCode != 0 || !stdout.Contains("\"ok\""))
            {
                var hint = _workerContainerId != null
                    ? "Container in Docker-in-Docker could not reach the agent API server on the worker container. The bridge network attach may have failed, or the worker's container ID is not resolvable."
                    : "Container could not reach the agent API server on the host. On Linux, this needs `host.docker.internal:host-gateway` (added automatically). If it still fails, the host firewall is blocking the listener port.";
                return Fail(
                    $"wget failed (exit {exitCode}): stdout='{Truncate(stdout, 200)}', stderr='{Truncate(stderr, 200)}'",
                    FailureKind.AgentApiUnreachable,
                    hint);
            }

            return Ok("Container reached agent API server");
        }
        catch (Exception ex)
        {
            return Fail($"Reachability check failed: {ex.Message}",
                FailureKind.AgentApiUnreachable, null);
        }
    }

    private async Task<DiagnosticStepResult> CleanupAsync(CancellationToken ct)
    {
        var errors = new List<string>();

        if (_testContainerId != null)
        {
            try
            {
                await _client.Containers.RemoveContainerAsync(_testContainerId,
                    new ContainerRemoveParameters { Force = true }, ct);
            }
            catch (Exception ex)
            {
                errors.Add($"container: {ex.Message}");
            }
            _testContainerId = null;
        }

        if (_testNetworkName != null)
        {
            if (_workerContainerId != null)
            {
                try
                {
                    await _client.Networks.DisconnectNetworkAsync(_testNetworkName, new NetworkDisconnectParameters
                    {
                        Container = _workerContainerId,
                        Force = true
                    }, ct);
                }
                catch (Exception ex)
                {
                    errors.Add($"detach worker: {ex.Message}");
                }
            }

            try
            {
                await _client.Networks.DeleteNetworkAsync(_testNetworkName, ct);
            }
            catch (Exception ex)
            {
                errors.Add($"network: {ex.Message}");
            }
            _testNetworkName = null;
        }

        if (_apiServer != null)
        {
            try
            {
                await _apiServer.DisposeAsync();
            }
            catch (Exception ex)
            {
                errors.Add($"api server: {ex.Message}");
            }
            _apiServer = null;
        }

        if (errors.Count > 0)
        {
            return new DiagnosticStepResult
            {
                Name = "",
                Success = false,
                Message = "Cleanup had errors: " + string.Join("; ", errors),
                Failure = FailureKind.None,
                Suggestion = "Stale containers or networks may need manual cleanup. List leftovers with `docker network ls --filter label=ilmarinen.diagnostic.pid`."
            };
        }

        return Ok("Cleaned up containers and networks");
    }

    private async Task<(string stdout, string stderr, int exitCode)> ExecAsync(
        string containerId, IList<string> cmd)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var exitCode = await DockerExec.RunAsync(_client, containerId, cmd, workDir: null, _userSpec, (isStdout, chunk) =>
        {
            if (isStdout)
            {
                stdout.Append(chunk);
            }
            else
            {
                stderr.Append(chunk);
            }
            return Task.CompletedTask;
        });

        return (stdout.ToString(), stderr.ToString(), exitCode);
    }

    private static DiagnosticStepResult Ok(string message) => new()
    {
        Name = "",
        Success = true,
        Message = message,
        Failure = FailureKind.None,
        Duration = TimeSpan.Zero
    };

    private static DiagnosticStepResult Fail(string message, FailureKind kind, string? suggestion) => new()
    {
        Name = "",
        Success = false,
        Message = message,
        Failure = kind,
        Suggestion = suggestion,
        Duration = TimeSpan.Zero
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Best-effort cleanup if RunAsync didn't run / threw before cleanup step.
        try { await CleanupAsync(default); } catch { }
        _client.Dispose();
    }
}
