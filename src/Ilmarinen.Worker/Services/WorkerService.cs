using Docker.DotNet;
using Ilmarinen.Docker;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.Worker.Services;

public class WorkerService : BackgroundService
{
    private readonly WorkerConfig _config;
    private readonly Guid _workerId;
    private readonly WorkspaceManager _workspaceManager;
    private readonly IWorkerDiagnostic _diagnostic;
    private readonly ILogger<WorkerService> _logger;
    private readonly MessageBuffer _messageBuffer = new();
    private HubConnection? _connection;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningJobs = new();

    // Single-activity gate. Job assignment and diagnostic each call TryEnter(...) and refuse if the worker is already doing the other thing.
    private readonly object _activityLock = new();
    private WorkerActivity _activity = WorkerActivity.Idle;
    private Guid? _currentJobId;

    private DiagnosticReport? _lastDiagnostic;
    private CancellationToken _stoppingToken;

    private Guid? CurrentJobId
    {
        get { lock (_activityLock) return _activity == WorkerActivity.RunningJob ? _currentJobId : null; }
    }

    private bool TryEnterActivity(WorkerActivity activity, Guid? jobId)
    {
        lock (_activityLock)
        {
            if (_activity != WorkerActivity.Idle) return false;
            _activity = activity;
            _currentJobId = jobId;
            return true;
        }
    }

    private void ExitActivity()
    {
        lock (_activityLock)
        {
            _activity = WorkerActivity.Idle;
            _currentJobId = null;
        }
    }

    public WorkerService(
        WorkerConfig config,
        WorkspaceManager workspaceManager,
        IWorkerDiagnostic diagnostic,
        ILogger<WorkerService> logger)
    {
        _config = config;
        _workerId = config.GetWorkerId();
        _workspaceManager = workspaceManager;
        _diagnostic = diagnostic;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        Directory.CreateDirectory(_config.WorkspacePath);

        // Discover Docker environment (host path for bind mounts, container ID for networking)
        var (hostPath, containerId) = await DiscoverDockerEnvironmentAsync();
        _config.HostWorkspacePath = hostPath;
        _config.WorkerContainerId = containerId;

        _connection = new HubConnectionBuilder()
            .WithUrl($"{_config.ServerUrl}/hub/workers")
            .WithAutomaticReconnect()
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
            })
            .Build();

        _connection.KeepAliveInterval = TimeSpan.FromSeconds(15);
        _connection.ServerTimeout = TimeSpan.FromSeconds(60);

        _connection.On<JobAssignment>("AssignJob", OnJobAssigned);
        _connection.On<string>("CancelJob", OnCancelJob);
        _connection.On<string>("DeleteWorkspace", OnDeleteWorkspace);
        _connection.On("RunDiagnostic", OnRunDiagnostic);

        _connection.Reconnecting += _ =>
        {
            _logger.LogWarning("Connection lost, attempting to reconnect...");
            return Task.CompletedTask;
        };

        _connection.Reconnected += async _ =>
        {
            _logger.LogInformation("Reconnected to server, re-authenticating...");
            try
            {
                await ConnectAndSync();
            }
            catch (Exception ex)
            {
                // If post-reconnect sync fails, force the connection to disconnected state so the heartbeat loop manually reconnects rather than leaving us as a zombie (connected, but server doesn't have our auth/diagnostic state).
                _logger.LogError(ex, "ConnectAndSync after reconnect failed; tearing down connection to retry");
                try { await _connection!.StopAsync(); } catch { }
            }
        };

        await ConnectWithRetryAsync(stoppingToken);
        try
        {
            await ConnectAndSync();
        }
        catch (Exception ex)
        {
            // ConnectAndSync does significant I/O (diagnostic + ReportDiagnostic). Don't fault the BackgroundService on a transient failure here — let the heartbeat loop notice the disconnected state and retry.
            _logger.LogError(ex, "Initial ConnectAndSync failed; tearing down to retry via heartbeat loop");
            try { await _connection.StopAsync(stoppingToken); } catch { }
        }

        _logger.LogInformation("Worker {WorkerId} connected and ready", _workerId);

        // Heartbeat loop — also handles manual reconnection when auto-reconnect gives up
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

                if (_connection.State == HubConnectionState.Disconnected)
                {
                    _logger.LogWarning("Connection lost, reconnecting...");
                    await ConnectWithRetryAsync(stoppingToken);
                    await ConnectAndSync();
                    _logger.LogInformation("Worker {WorkerId} reconnected and ready", _workerId);
                }
                else if (_connection.State == HubConnectionState.Connected)
                {
                    await _connection.SendAsync("Heartbeat", new WorkerHeartbeat
                    {
                        WorkerId = _workerId
                    }, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Heartbeat failed");
            }
        }
    }

    private async Task ConnectWithRetryAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Connecting to {ServerUrl}...", _config.ServerUrl);
                await _connection!.StartAsync(ct);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect, retrying in 5 seconds...");
                await Task.Delay(5000, ct);
            }
        }
    }

    /// <summary>
    /// Authenticate with the server (mutual ECDSA challenge-response).
    /// </summary>
    private async Task Authenticate()
    {
        // Step 1: Initiate connection with our nonce, get challenge
        var workerNonce = RandomNumberGenerator.GetBytes(32);
        var challenge = await _connection!.InvokeAsync<AuthChallenge>("Connect", new WorkerConnect
        {
            WorkerId = _workerId,
            ProtocolHash = ProtocolVersion.Hash,
            Nonce = workerNonce
        });

        // Step 2: Verify protocol compatibility
        if (challenge.ProtocolHash != ProtocolVersion.Hash)
        {
            _logger.LogError(
                "Protocol mismatch: worker is '{WorkerHash}', server is '{ServerHash}'. Rebuild both with the same protocol definitions.",
                ProtocolVersion.Hash, challenge.ProtocolHash);
            throw new InvalidOperationException(
                $"Protocol mismatch. Worker is '{ProtocolVersion.Hash}', server is '{challenge.ProtocolHash}'. " +
                "Rebuild both with the same protocol definitions.");
        }

        // Step 3: Verify server identity (server signed both nonces)
        using var serverKey = _config.GetServerPublicKey();
        var challengeData = Encoding.UTF8.GetBytes(
            $"{_workerId}:{Convert.ToBase64String(workerNonce)}:{Convert.ToBase64String(challenge.Nonce)}");

        if (!serverKey.VerifyData(challengeData, challenge.ServerSignature, HashAlgorithmName.SHA256))
        {
            _logger.LogError("Server signature verification failed — wrong server or MITM?");
            throw new InvalidOperationException("Server identity verification failed.");
        }

        // Step 4: Sign the same data and authenticate
        using var workerKey = _config.GetWorkerPrivateKey();
        var signature = workerKey.SignData(challengeData, HashAlgorithmName.SHA256);

        await _connection!.SendAsync("Authenticate", new WorkerAuthenticate
        {
            Signature = signature,
            Workspaces = _workspaceManager.DiscoverWorkspaces()
        });
    }

    /// <summary>
    /// Re-authenticate with the server, replay buffered messages, reconcile state,
    /// run the startup diagnostic on first connect (cached on subsequent connects),
    /// and signal ready only if no job is running and the diagnostic passed.
    /// </summary>
    private async Task ConnectAndSync()
    {
        await Authenticate();

        // Replay buffered messages first so the server has accurate state before we call Reconnect (e.g., a buffered JobCompleted).
        while (_messageBuffer.TryPeek(out var msg))
        {
            try
            {
                await _connection!.InvokeCoreAsync(msg!.Method, msg.Args);
                _messageBuffer.TryDequeue(out _);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to replay {Method}, will retry on next reconnect", msg!.Method);
                return;
            }
        }

        // Reconcile state with server
        var currentJob = CurrentJobId;
        var response = await _connection!.InvokeAsync<ReconnectResponse>("Reconnect", new WorkerReconnect
        {
            RunningJobId = currentJob
        });

        // If we're running a job the server doesn't expect, abort it. This happens when the job was cancelled or failed while we were disconnected.
        if (currentJob != null && response.ExpectedJobId != currentJob)
        {
            _logger.LogInformation(
                "Server does not expect job {JobId} (expected {ExpectedJobId}), aborting",
                currentJob, response.ExpectedJobId);
            OnCancelJob(currentJob.Value.ToString());
            _messageBuffer.Clear();
            return;
        }

        // If a job is running we don't gate it on the diagnostic — the in-flight job is the source of truth. Skip Ready (the existing contract) and skip the diagnostic.
        if (currentJob != null)
            return;

        // First connect ever: run the diagnostic. Subsequent reconnects re-push the cached result so the server reconstructs its state without re-running (and without the per-reconnect cost of an image pull / container run).
        if (_lastDiagnostic == null)
        {
            _logger.LogInformation("Running startup diagnostic...");
            try
            {
                _lastDiagnostic = await _diagnostic.RunAsync(_config.WorkerContainerId, _stoppingToken);
                _logger.LogInformation("Startup diagnostic: {Status} - {Summary}",
                    _lastDiagnostic.Status, _lastDiagnostic.Summary);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Diagnostic itself threw — treating as Unhealthy");
                _lastDiagnostic = new DiagnosticReport
                {
                    Status = DiagnosticStatus.Unhealthy,
                    Summary = $"Diagnostic threw: {ex.Message}",
                    Steps = [],
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        await _connection!.SendAsync("ReportDiagnostic", _lastDiagnostic);

        if (_lastDiagnostic.Status == DiagnosticStatus.Healthy)
        {
            await _connection!.SendAsync("Ready");
        }
        else
        {
            _logger.LogWarning("Worker is online but not functional: {Summary}", _lastDiagnostic.Summary);
        }
    }

    private Task OnRunDiagnostic()
    {
        // Fire-and-forget so the SignalR dispatch loop stays unblocked.
        _ = ExecuteDiagnosticAsync();
        return Task.CompletedTask;
    }

    private async Task ExecuteDiagnosticAsync()
    {
        if (!TryEnterActivity(WorkerActivity.RunningDiagnostic, jobId: null))
        {
            // Race: a job arrived (or was already running) before the worker received the RunDiagnostic request. The server marked us not-ready preparing to fire the diagnostic, but a job was already in-flight. Re-push the cached report so the UI doesn't sit on stale "Running" or pre-click state, and re-publish ready-on-healthy so the worker can pick up more jobs.
            _logger.LogInformation("Cannot run diagnostic: worker is busy");
            if (_lastDiagnostic != null)
            {
                await SendReliableAsync("ReportDiagnostic", _lastDiagnostic);
                if (_lastDiagnostic.Status == DiagnosticStatus.Healthy)
                    await SendReliableAsync("Ready");
            }
            return;
        }

        try
        {
            // The "Running" placeholder is a transient UI hint; don't buffer it. If the connection is dropped while it's queued, replaying it later (after the real result has already arrived) would briefly clobber the final report with a stale "Running" status.
            try
            {
                if (_connection?.State == HubConnectionState.Connected)
                {
                    await _connection.SendAsync("ReportDiagnostic", new DiagnosticReport
                    {
                        Status = DiagnosticStatus.Running,
                        Summary = "Running diagnostic...",
                        Steps = [],
                        CheckedAt = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not push Running placeholder; will push final report when ready");
            }

            DiagnosticReport report;
            try
            {
                report = await _diagnostic.RunAsync(_config.WorkerContainerId, _stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Diagnostic threw");
                report = new DiagnosticReport
                {
                    Status = DiagnosticStatus.Unhealthy,
                    Summary = $"Diagnostic threw: {ex.Message}",
                    Steps = [],
                    CheckedAt = DateTime.UtcNow
                };
            }

            _lastDiagnostic = report;
            await SendReliableAsync("ReportDiagnostic", report);

            // Server flipped us not-ready before invoking the diagnostic. If we're healthy, ask to be marked ready again. If we're unhealthy, stay not-ready.
            if (report.Status == DiagnosticStatus.Healthy)
                await SendReliableAsync("Ready");
        }
        finally
        {
            ExitActivity();
        }
    }

    private Task OnJobAssigned(JobAssignment job)
    {
        if (!TryEnterActivity(WorkerActivity.RunningJob, job.Id))
        {
            _logger.LogWarning(
                "Job {JobId} assigned but worker is busy (current activity: {Activity}). Reporting failure.",
                job.Id, _activity);
            _ = SendReliableAsync("JobCompleted", job.Id, new JobResult
            {
                Id = job.Id,
                Status = JobStatus.Failed,
                Duration = TimeSpan.Zero,
                Workspaces = _workspaceManager.DiscoverWorkspaces()
            });
            return Task.CompletedTask;
        }

        // Return immediately so the SignalR dispatch loop stays unblocked (otherwise CancelJob messages can't be delivered while a job is running)
        _ = ExecuteJobAsync(job);
        return Task.CompletedTask;
    }

    private async Task ExecuteJobAsync(JobAssignment job)
    {
        _logger.LogInformation("Received job {JobId}: {RepoUrl} @ {Ref}", job.Id, job.RepoUrl, job.Ref);

        var startTime = DateTime.UtcNow;
        var logCollector = new LogCollector(job.Id, _connection!, _messageBuffer);
        var jobKey = job.Id.ToString();
        using var cts = new CancellationTokenSource();
        _runningJobs[jobKey] = cts;

        // Use try/finally to guarantee activity-state cleanup. If a catch handler itself throws (e.g. WorkspaceManager.DiscoverWorkspaces failing during shutdown), we would otherwise leave _activity stuck at RunningJob and refuse all future work.
        JobResult result;
        try
        {
            try
            {
                await SendReliableAsync("JobStarted", job.Id);

                var runner = new JobRunner(_config, _workspaceManager, job, _connection!, _logger, logCollector);
                var runResult = await runner.ExecuteAsync(cts.Token);

                var status = cts.IsCancellationRequested ? JobStatus.Cancelled : runResult.Status;

                result = runResult with
                {
                    Status = status,
                    Duration = DateTime.UtcNow - startTime,
                    Workspaces = SafeDiscoverWorkspaces()
                };

                await logCollector.FlushAsync();
                _logger.LogInformation("Job {JobId} completed with status {Status}", job.Id, result.Status);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                _logger.LogInformation("Job {JobId} was cancelled", job.Id);

                logCollector.WriteStderr("Job cancelled.");
                await TryFlushLogsAsync(logCollector);

                result = new JobResult
                {
                    Id = job.Id,
                    Status = JobStatus.Cancelled,
                    Duration = DateTime.UtcNow - startTime,
                    Workspaces = SafeDiscoverWorkspaces()
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job {JobId} failed with exception", job.Id);

                logCollector.WriteStderr($"Job failed with exception: {ex}");
                await TryFlushLogsAsync(logCollector);

                result = new JobResult
                {
                    Id = job.Id,
                    Status = JobStatus.Failed,
                    Duration = DateTime.UtcNow - startTime,
                    Workspaces = SafeDiscoverWorkspaces()
                };
            }
        }
        finally
        {
            // Clear activity BEFORE notifying the server so that when the server marks us ready and immediately assigns the next job, our state machine accepts it. Done in finally so we don't leak the activity flag if a catch handler throws.
            _runningJobs.TryRemove(jobKey, out _);
            ExitActivity();
        }

        // The server's JobCompleted handler marks this worker ready and dispatches the next queued job. Sending an explicit Ready here too would double-trigger dispatch and could stack a second job on this worker.
        await SendReliableAsync("JobCompleted", job.Id, result);
    }

    private IReadOnlyList<WorkspaceInfo> SafeDiscoverWorkspaces()
    {
        try { return _workspaceManager.DiscoverWorkspaces(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DiscoverWorkspaces failed; continuing with empty list");
            return [];
        }
    }

    /// <summary>
    /// Sends a message to the server with implicit acknowledgment via InvokeAsync.
    /// If the server is unreachable, the message is buffered for replay on reconnection.
    /// </summary>
    private async Task SendReliableAsync(string method, params object[] args)
    {
        if (_connection?.State != HubConnectionState.Connected)
        {
            _messageBuffer.Enqueue(new BufferedMessage { Method = method, Args = args });
            return;
        }

        try
        {
            await _connection.InvokeCoreAsync(method, args);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send {Method}, buffering for replay", method);
            _messageBuffer.Enqueue(new BufferedMessage { Method = method, Args = args });
        }
    }

    private static async Task TryFlushLogsAsync(LogCollector logCollector)
    {
        try
        {
            await logCollector.FlushAsync();
        }
        catch
        {
            // Connection may be down — chunks are in the message buffer
        }
    }

    private void OnCancelJob(string jobId)
    {
        _logger.LogInformation("Received cancel request for job {JobId}", jobId);
        if (_runningJobs.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
        }
        else
        {
            _logger.LogWarning("Cancel requested for job {JobId} but it is not running on this worker", jobId);
        }
    }

    private async Task OnDeleteWorkspace(string name)
    {
        _logger.LogInformation("Received workspace deletion request: {WorkspaceName}", name);
        var result = _workspaceManager.TryDelete(name);
        await _connection!.SendAsync("WorkspaceDeleted", name, result);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_connection != null)
        {
            try
            {
                await _connection.StopAsync(cancellationToken);
            }
            catch (ObjectDisposedException) { }

            try
            {
                await _connection.DisposeAsync();
            }
            catch (ObjectDisposedException) { }
        }

        await base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Discovers the host-side path for the workspace when running inside Docker.
    /// Uses Docker inspect on own container to find the mount source path.
    /// Also captures the worker container ID for Docker-in-Docker networking.
    /// </summary>
    private async Task<(string hostPath, string? containerId)> DiscoverDockerEnvironmentAsync()
    {
        // Docker creates /.dockerenv in every container it starts. Probe the file rather than inferring containerization from a failed inspect: the two cases need opposite handling, and a failed inspect cannot tell "not a container" apart from "a container whose socket is unreachable" — treating the latter as the former is what silently mounts an empty workspace.
        if (!File.Exists("/.dockerenv"))
        {
            return (_config.WorkspacePath, null);
        }

        using var client = DockerClientFactory.Create();

        // Docker sets the hostname to the container ID unless it is overridden — which is why the deployment must not set `hostname:`.
        var containerId = System.Net.Dns.GetHostName();
        var inspect = await client.Containers.InspectContainerAsync(containerId);

        var hostPath = inspect.Mounts?.FirstOrDefault(m => m.Destination == _config.WorkspacePath)?.Source;
        if (string.IsNullOrEmpty(hostPath))
        {
            throw new InvalidOperationException($"Running in a container, but no Docker mount backs the workspace path '{_config.WorkspacePath}'. Mount a volume whose destination matches ILMARINEN_WORKSPACE_PATH exactly. Without it, jobs would bind-mount a host path that does not exist and every pipeline would run against an empty /workspace.");
        }

        _logger.LogInformation("Running in Docker container: {ContainerId}", containerId);
        _logger.LogInformation("Discovered host workspace path: {HostPath} -> {ContainerPath}", hostPath, _config.WorkspacePath);
        return (hostPath, containerId);
    }
}

internal enum WorkerActivity
{
    Idle,
    RunningJob,
    RunningDiagnostic
}
