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
    private readonly SleepInhibitor _sleepInhibitor;
    private readonly UpdateSignal _updateSignal;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<WorkerService> _logger;
    private readonly MessageBuffer _messageBuffer = new();
    private HubConnection? _connection;
    private BufferedHubSender? _sender;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningJobs = new();

    // Held while a job's result is reported and while a reconnect reconciles state, so the two can't interleave. See ReportCompletionAsync.
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    // Everything the worker has to finish before the process may exit: a job tearing its containers down, or a rejection telling the server about work it can't take. Entries clear themselves, so this stays the size of what is actually in flight.
    private readonly ConcurrentDictionary<long, Task> _inFlight = new();
    private long _inFlightSequence;

    // Single-activity gate. Job assignment and diagnostic each call TryEnter(...) and refuse if the worker is already doing the other thing.
    private static readonly TimeSpan RetryPollInterval = TimeSpan.FromSeconds(5);

    private readonly object _activityLock = new();
    private WorkerActivity _activity = WorkerActivity.Idle;
    private Guid? _currentJobId;

    // Read by the retry loop while the SignalR dispatch thread writes it.
    private volatile DiagnosticReport? _lastDiagnostic;
    private CancellationToken _stoppingToken;

    // True once an authenticated server has told us it serves a different bundle than the one we run. Only meaningful in launcher mode (BundleHash set).
    private volatile bool _updatePending;
    private int _handshakeFailures;

    private Guid? CurrentJobId
    {
        get { lock (_activityLock) return _activity == WorkerActivity.RunningJob ? _currentJobId : null; }
    }

    private bool TryEnterActivity(WorkerActivity activity, Guid? jobId, out WorkerActivity blockingActivity, out Guid? blockingJobId)
    {
        lock (_activityLock)
        {
            blockingActivity = _activity;
            blockingJobId = _currentJobId;
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
        SleepInhibitor sleepInhibitor,
        UpdateSignal updateSignal,
        IHostApplicationLifetime lifetime,
        ILogger<WorkerService> logger)
    {
        _config = config;
        _workerId = config.GetWorkerId();
        _workspaceManager = workspaceManager;
        _diagnostic = diagnostic;
        _sleepInhibitor = sleepInhibitor;
        _updateSignal = updateSignal;
        _lifetime = lifetime;
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
        _sender = new BufferedHubSender(_connection, _messageBuffer, _logger);

        _connection.KeepAliveInterval = TimeSpan.FromSeconds(15);
        _connection.ServerTimeout = TimeSpan.FromSeconds(60);

        _connection.On<JobAssignment>("AssignJob", OnJobAssigned);
        _connection.On<string>("CancelJob", OnCancelJob);
        _connection.On<string>("DeleteWorkspace", OnDeleteWorkspace);
        _connection.On("RunDiagnostic", OnRunDiagnostic);
        _connection.On("RecheckHost", OnRecheckHost);

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

        var diagnosticRetries = RetryUnhealthyDiagnosticAsync(stoppingToken);

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
                    try
                    {
                        await ConnectAndSync();
                        _logger.LogInformation("Worker {WorkerId} reconnected and ready", _workerId);
                    }
                    catch (Exception ex)
                    {
                        // Same zombie hazard as the Reconnected handler: without a teardown, the connection stays Connected-but-unauthenticated and this loop would happily heartbeat into the void forever.
                        _logger.LogError(ex, "ConnectAndSync after manual reconnect failed; tearing down connection to retry");
                        try
                        {
                            await _connection.StopAsync(stoppingToken);
                        }
                        catch (Exception stopEx)
                        {
                            _logger.LogDebug(stopEx, "Teardown StopAsync failed; connection state will settle via the next loop iteration");
                        }
                    }
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

        await diagnosticRetries;
        await DrainInFlightWorkAsync();
    }

    private void TrackInFlight(Task work)
    {
        var key = Interlocked.Increment(ref _inFlightSequence);
        _inFlight[key] = work;
        _ = work.ContinueWith(_ => _inFlight.TryRemove(key, out _), TaskScheduler.Default);
    }

    /// <summary>
    /// Waits for work the stop signal just cancelled to tear itself down and report. Best effort: the host's shutdown
    /// timeout bounds it, and a sync stuck against an unreachable server can run that out, because the worker's hub
    /// calls don't take the stopping token.
    /// </summary>
    private async Task DrainInFlightWorkAsync()
    {
        // Looped rather than a single snapshot: a job assigned in the shutdown window is refused, and that refusal is itself work that has to reach the server. It terminates because nothing new is accepted once the stopping token is set.
        while (true)
        {
            var pending = _inFlight.Values.Where(work => !work.IsCompleted).ToList();
            if (pending.Count == 0)
            {
                return;
            }

            _logger.LogInformation("Waiting for {Count} job task(s) to finish before shutting down", pending.Count);
            try
            {
                await Task.WhenAll(pending);
            }
            catch (Exception ex)
            {
                // Nothing else observes these tasks, so a fault here would otherwise vanish.
                _logger.LogError(ex, "A job faulted while the worker was shutting down");
            }
        }
    }

    /// <summary>
    /// Re-runs a diagnostic that came back unhealthy. Nothing else will: the result is cached for the life of the process, and only an operator clicking the button asks for another — so a worker that started while DNS was down, or while the Docker daemon was restarting, stays out of the fleet until someone notices it.
    /// </summary>
    private async Task RetryUnhealthyDiagnosticAsync(CancellationToken stoppingToken)
    {
        var consecutiveFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!ShouldRetryDiagnostic())
                {
                    // Nothing to retry now. The count resets so that a worker coming back from an outage waits seconds rather than minutes for its first attempt.
                    consecutiveFailures = 0;
                    await Task.Delay(RetryPollInterval, stoppingToken);
                    continue;
                }

                var delay = DiagnosticPolicy.Jitter(DiagnosticPolicy.RetryDelay(consecutiveFailures + 1));
                await Task.Delay(delay, stoppingToken);

                // Re-checked after the wait: an operator may have run one, or the worker may have gone offline or started draining. The count only advances when an attempt actually happens, or a long outage would back the worker off to the ceiling without ever trying.
                if (!ShouldRetryDiagnostic())
                {
                    continue;
                }

                consecutiveFailures++;
                _logger.LogInformation("Rerunning the diagnostic that left this worker out of the fleet (attempt {Attempt})", consecutiveFailures);
                await ExecuteDiagnosticAsync(DiagnosticTrigger.Automatic);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Diagnostic retry failed");
            }
        }
    }

    private bool ShouldRetryDiagnostic()
    {
        return DiagnosticPolicy.ShouldRetry(_lastDiagnostic, _connection?.State == HubConnectionState.Connected, _updatePending);
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
            Nonce = workerNonce,
            BundleHash = _config.BundleHash
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

        // Act only when the server has an opinion (null = no bundle configured, which must never drain the fleet). Assigning rather than or-ing lets a rollback to our own bundle clear a previously pending update. Note the handshake authenticates the peer, not this field — the channel is plaintext, so a MITM can tamper with it; that only causes spurious or suppressed restarts, never code execution (the launcher verifies bundle signatures).
        if (_config.BundleHash != null && challenge.CurrentBundleHash != null)
        {
            _updatePending = challenge.CurrentBundleHash != _config.BundleHash;
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
        try
        {
            await ConnectAndSyncCore();
            Interlocked.Exchange(ref _handshakeFailures, 0);
        }
        catch
        {
            RecordHandshakeFailure();
            throw;
        }
    }

    /// <summary>
    /// In launcher mode, persistent handshake failure means the server most likely moved to an incompatible build (a protocol break prevents us from ever learning the new bundle hash the normal way), so exit and let the launcher fetch the current bundle. The launcher backs off when the manifest hash turns out unchanged, so a spurious exit here (server down, revoked key) costs nothing but a restart.
    /// </summary>
    private void RecordHandshakeFailure()
    {
        if (_config.BundleHash == null || CurrentJobId != null)
        {
            return;
        }

        var failures = Interlocked.Increment(ref _handshakeFailures);

        // A buffered message (typically a job result) dies with the process, so hold out far longer when the buffer is non-empty — exiting would mark a finished job Failed. Not forever, though: across a genuine protocol break the buffer is undeliverable anyway.
        var threshold = _messageBuffer.IsEmpty ? 3 : 20;
        if (failures >= threshold)
        {
            RequestUpdateExit($"{failures} consecutive handshake failures");
        }
    }

    private void RequestUpdateExit(string reason)
    {
        _logger.LogInformation("Exiting for bundle update: {Reason}", reason);
        _updateSignal.UpdateRequired = true;
        _lifetime.StopApplication();
    }

    private async Task ConnectAndSyncCore()
    {
        await Authenticate();

        // Ahead of anything the server might act on. A replayed JobCompleted marks this worker ready again, so the server has to know whether we can take that work before it decides — otherwise a worker benched by a failed diagnostic is handed the next job the moment it reconnects.
        var cached = _lastDiagnostic;
        if (cached != null)
        {
            await _connection!.SendAsync("ReportDiagnostic", cached);
        }

        // Replaying the buffer and reconciling are one step: a job reporting itself in the middle would be refused as unauthenticated, land back in the buffer after the replay had passed it, and then be contradicted by a Reconnect that says no job is running. Authentication stays outside the lock — it is the part a finishing job has to be able to wait for.
        Guid? currentJob;
        ReconnectResponse response;
        await _syncLock.WaitAsync();
        try
        {
            // Replay buffered messages first so the server has accurate state before we call Reconnect (e.g., a buffered JobCompleted).
            await _messageBuffer.ReplayAsync(msg => _connection!.InvokeCoreAsync(msg.Method, msg.Args), _logger);

            // Reconcile state with server
            currentJob = CurrentJobId;
            response = await _connection!.InvokeAsync<ReconnectResponse>("Reconnect", new WorkerReconnect
            {
                RunningJobId = currentJob
            });
        }
        finally
        {
            _syncLock.Release();
        }

        // If we're running a job the server doesn't expect, abort it. This happens when the job was cancelled or failed while we were disconnected.
        if (currentJob != null && response.ExpectedJobId != currentJob)
        {
            _logger.LogInformation(
                "Server does not expect job {JobId} (expected {ExpectedJobId}), aborting",
                currentJob, response.ExpectedJobId);
            OnCancelJob(currentJob.Value.ToString());

            // What is left in the buffer was enqueued during this sync — the replay above drained everything older. It is the aborted job's last log output, which is worth keeping: the next sync delivers it, and the server persists chunks for a job that has already finished.
            return;
        }

        // If a job is running we don't gate it on the diagnostic — the in-flight job is the source of truth. Skip Ready (the existing contract) and skip the diagnostic.
        if (currentJob != null)
            return;

        // Idle on a stale bundle: exit now, before Ready, so the server never dispatches to a worker that is about to vanish. The buffer replay above already delivered anything pending (a buffered JobCompleted included), so nothing is lost.
        if (_updatePending)
        {
            RequestUpdateExit($"server bundle differs from running bundle {_config.BundleHash}");
            return;
        }

        // First connect ever: run the diagnostic, which reports its own result and readiness. Subsequent reconnects re-push the cached result (above) so the server reconstructs its state without re-running, and without the per-reconnect cost of an image pull / container run.
        if (cached == null)
        {
            await ExecuteDiagnosticAsync(DiagnosticTrigger.Automatic);
            return;
        }

        if (DiagnosticStatusPolicy.CanAcceptJobs(cached.Status))
        {
            await _connection!.SendAsync("Ready");
        }
    }

    private Task OnRunDiagnostic()
    {
        // Fire-and-forget so the SignalR dispatch loop stays unblocked.
        _ = ExecuteDiagnosticAsync(DiagnosticTrigger.Operator);
        return Task.CompletedTask;
    }

    /// <summary>The server asking whether this host is still fit for work, typically after a job failed on it. Same diagnostic, no operator waiting on it.</summary>
    private Task OnRecheckHost()
    {
        _ = ExecuteDiagnosticAsync(DiagnosticTrigger.Automatic);
        return Task.CompletedTask;
    }

    private async Task ExecuteDiagnosticAsync(DiagnosticTrigger trigger)
    {
        if (!TryEnterActivity(WorkerActivity.RunningDiagnostic, jobId: null, out var blockingActivity, out var blockingJobId))
        {
            // Race: a job arrived (or was already running) before the worker received the RunDiagnostic request. The server marked us not-ready preparing to fire the diagnostic, but a job was already in-flight. Re-push the cached report so the UI doesn't sit on stale "Running" or pre-click state, and re-publish ready-on-healthy so the worker can pick up more jobs.
            _logger.LogInformation("Cannot run diagnostic: worker is {Activity}{JobId}", blockingActivity, blockingJobId != null ? $" ({blockingJobId})" : "");
            if (_lastDiagnostic != null)
            {
                await SendDiagnosticIfConnectedAsync(_lastDiagnostic);
                if (DiagnosticStatusPolicy.CanAcceptJobs(_lastDiagnostic.Status) && !_updatePending)
                {
                    await SendReadyIfConnectedAsync();
                }
            }
            else
            {
                _logger.LogWarning("Worker has no diagnostic to report yet; it stays not-ready until it can run one");
            }
            return;
        }

        try
        {
            // The "Running" placeholder is a transient UI hint, and only answers an operator who just asked: shown for a diagnostic nobody asked for, it would keep painting an unhealthy worker as busy and grey out the button that reruns it. Don't buffer it either — replaying it after the real report has landed would clobber a final status with a stale "Running".
            try
            {
                if (trigger == DiagnosticTrigger.Operator && _connection?.State == HubConnectionState.Connected)
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

            _logger.LogInformation("Running diagnostic...");

            DiagnosticReport report;
            try
            {
                report = await _diagnostic.RunAsync(_config.WorkerContainerId, _stoppingToken);
            }
            catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
            {
                // Shutting down is not a verdict on the host, and recording one would leave the server holding a bogus Unhealthy that outlives the disconnect.
                _logger.LogInformation("Diagnostic cancelled by shutdown");
                return;
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

            _logger.LogInformation("Diagnostic: {Status} - {Summary}", report.Status, report.Summary);
            _lastDiagnostic = report;
            await SendDiagnosticIfConnectedAsync(report);

            // An operator-initiated run finds the server has already flipped us not-ready. If we can still work, ask to be marked ready again. If we're unhealthy, stay not-ready. And never ask while an update is pending — a draining worker must not attract jobs.
            if (DiagnosticStatusPolicy.CanAcceptJobs(report.Status) && !_updatePending)
            {
                if (report.Status == DiagnosticStatus.Degraded)
                {
                    _logger.LogWarning("Worker is functional but degraded: {Summary}", report.Summary);
                }
                await SendReadyIfConnectedAsync();
            }
            else if (!DiagnosticStatusPolicy.CanAcceptJobs(report.Status))
            {
                _logger.LogWarning("Worker is online but not functional: {Summary}", report.Summary);
            }
        }
        finally
        {
            ExitActivity();
        }
    }

    private Task OnJobAssigned(JobAssignment job)
    {
        // The connection outlives the stop signal so a job in flight can report itself, which leaves a window where the server still sees us ready. Taking work now would run it on an already-cancelled token; refuse it outright so it reads as a failure the server can retry rather than a job nobody ever hears about again.
        if (_stoppingToken.IsCancellationRequested)
        {
            TrackInFlight(RejectJobAsync(job, "the worker is shutting down"));
            return Task.CompletedTask;
        }

        if (!TryEnterActivity(WorkerActivity.RunningJob, job.Id, out var blockingActivity, out var blockingJobId))
        {
            TrackInFlight(RejectBusyJobAsync(job, blockingActivity, blockingJobId));
            return Task.CompletedTask;
        }

        // Return immediately so the SignalR dispatch loop stays unblocked (otherwise CancelJob messages can't be delivered while a job is running)
        TrackInFlight(ExecuteJobAsync(job));
        return Task.CompletedTask;
    }

    private async Task RejectBusyJobAsync(JobAssignment job, WorkerActivity blockingActivity, Guid? blockingJobId)
    {
        if (blockingJobId == job.Id)
        {
            // The server redelivered the job we're already running. Don't write into its live log stream — a second collector for the same job would restart sequence numbers and collide with the running one's chunks.
            _logger.LogWarning("Job {JobId} assigned but it is already running on this worker. Reporting failure.", job.Id);
            await ReportRejectionAsync(job);
            return;
        }

        await RejectJobAsync(job, $"the worker is busy (current activity: {blockingActivity})");
    }

    private async Task RejectJobAsync(JobAssignment job, string reason)
    {
        var logCollector = new LogCollector(job.Id, _sender!, _logger);
        var jobLogger = new LoggerTee(_logger, logCollector);
        jobLogger.LogWarning("Job {JobId} assigned but {Reason}. Reporting failure.", job.Id, reason);
        await logCollector.FlushAsync();

        await ReportRejectionAsync(job);
    }

    private async Task ReportRejectionAsync(JobAssignment job)
    {
        await _sender!.SendOrBufferAsync("JobCompleted", job.Id, new JobResult
        {
            Id = job.Id,
            Status = JobStatus.Failed,
            Duration = TimeSpan.Zero,
            Workspaces = SafeDiscoverWorkspaces()
        });
    }

    private async Task ExecuteJobAsync(JobAssignment job)
    {
        var startTime = DateTime.UtcNow;
        var logCollector = new LogCollector(job.Id, _sender!, _logger);
        var jobLogger = new LoggerTee(_logger, logCollector);
        var jobKey = job.Id.ToString();

        jobLogger.LogInformation("Received job {JobId}: {RepoUrl} @ {Ref}", job.Id, job.RepoUrl, job.Ref);

        // Two tokens. CancelJob trips the first; the job runs on the second, which the host's shutdown trips as well — otherwise a SIGTERM mid-job leaves the step container running with nothing left to tear it down.
        using var cancel = new CancellationTokenSource();
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, _stoppingToken);
        _runningJobs[jobKey] = cancel;

        // Use try/finally to guarantee activity-state cleanup. If a catch handler itself throws (e.g. WorkspaceManager.DiscoverWorkspaces failing during shutdown), we would otherwise leave _activity stuck at RunningJob and refuse all future work.
        JobResult result;
        try
        {
            try
            {
                // Keep the host from auto-suspending out from under us for as long as the job runs.
                using var inhibitor = await _sleepInhibitor.AcquireAsync($"Ilmarinen job {job.Id}");

                await _sender!.SendOrBufferAsync("JobStarted", job.Id);

                var runner = new JobRunner(_config, _workspaceManager, job, _sender!, jobLogger, logCollector);
                var runResult = await runner.ExecuteAsync(jobCts.Token);

                // A pipeline that ran to the end reports what it actually did, even if the worker is on its way out by now.
                var status = cancel.IsCancellationRequested ? JobStatus.Cancelled : runResult.Status;

                result = runResult with
                {
                    Status = status,
                    Duration = DateTime.UtcNow - startTime,
                    Workspaces = SafeDiscoverWorkspaces()
                };

                jobLogger.LogInformation("Job {JobId} completed with status {Status}", job.Id, result.Status);
                await logCollector.FlushAsync();
            }
            catch (OperationCanceledException) when (jobCts.IsCancellationRequested)
            {
                // Cancelled means a person asked for it. A job the worker dropped because its host is going down is a failure, and reporting it as one now is what keeps it from sitting Running until a reconnect that may never come.
                var abandoned = !cancel.IsCancellationRequested;
                if (abandoned)
                {
                    jobLogger.LogWarning("Job {JobId} abandoned: the worker is shutting down", job.Id);
                }
                else
                {
                    jobLogger.LogInformation("Job {JobId} was cancelled", job.Id);
                }
                await logCollector.FlushAsync();

                result = new JobResult
                {
                    Id = job.Id,
                    Status = abandoned ? JobStatus.Failed : JobStatus.Cancelled,
                    Duration = DateTime.UtcNow - startTime,
                    Workspaces = SafeDiscoverWorkspaces()
                };
            }
            catch (Exception ex)
            {
                jobLogger.LogError(ex, "Job {JobId} failed with exception", job.Id);
                await logCollector.FlushAsync();

                result = new JobResult
                {
                    Id = job.Id,
                    Status = JobStatus.Failed,
                    Duration = DateTime.UtcNow - startTime,
                    Workspaces = SafeDiscoverWorkspaces()
                };
            }
        }
        catch
        {
            // Only reachable if a catch handler above threw — a log write, a workspace scan. The result is lost either way, but the worker must not be left stuck at RunningJob refusing all future work. On every other path ReportCompletionAsync leaves the activity, and it must not be left a second time: by the time it returns, the next job may already have claimed the slot.
            ExitActivity();
            throw;
        }
        finally
        {
            _runningJobs.TryRemove(jobKey, out _);
        }

        var delivered = await ReportCompletionAsync(job.Id, result);

        // Exit only on confirmed delivery: a buffered JobCompleted dies with this process and the job would be marked Failed on relaunch. When buffered, the reconnect path replays the buffer and then performs this drain itself.
        if (_updatePending && delivered)
        {
            RequestUpdateExit("job finished on a stale bundle");
        }
    }

    /// <summary>
    /// Leaves the activity and reports the result as one indivisible step. A reconnect's reconciliation reads the activity to decide whether this worker still holds its job; run between the two, it finds a worker with no job and no result yet, and the server fails a job that had in fact just finished — permanently, since Failed is final.
    ///
    /// Clearing the activity is also what lets the state machine accept the next job, which the server assigns the moment this report lands, so it has to happen before the report rather than after.
    /// </summary>
    private async Task<bool> ReportCompletionAsync(Guid jobId, JobResult result)
    {
        await _syncLock.WaitAsync();
        try
        {
            ExitActivity();

            // The server's JobCompleted handler marks this worker ready and dispatches the next queued job. Sending an explicit Ready here too would double-trigger dispatch and could stack a second job on this worker.
            return await _sender!.SendOrBufferAsync("JobCompleted", jobId, result);
        }
        finally
        {
            _syncLock.Release();
        }
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
    /// Ready is a state hint that ConnectAndSyncCore re-derives on every reconnect, so it must never be buffered: a replayed stale Ready would arrive after re-auth and mark a worker ready past the update-pending gates (same reasoning as the Running placeholder above).
    /// </summary>
    private async Task SendReadyIfConnectedAsync()
    {
        if (_connection?.State != HubConnectionState.Connected)
        {
            return;
        }

        try
        {
            await _connection.SendAsync("Ready");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not send Ready; the next reconnect re-derives it");
        }
    }

    /// <summary>
    /// A report is derived state that the worker re-pushes whenever it re-syncs, so one that can't be sent now is dropped rather than buffered: replayed after a reconnect, it would land on top of a newer report and undo it.
    /// </summary>
    private async Task SendDiagnosticIfConnectedAsync(DiagnosticReport report)
    {
        if (_connection?.State != HubConnectionState.Connected)
        {
            return;
        }

        try
        {
            await _connection.SendAsync("ReportDiagnostic", report);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not send diagnostic report; the next reconnect re-pushes it");
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
        // First: this signals the stopping token and waits for ExecuteAsync, which cancels the running job and waits for it to report. The connection has to outlive that, or the job it just killed would have no way to say so.
        await base.StopAsync(cancellationToken);

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

/// <summary>Who asked for a diagnostic. An operator is waiting for an answer to a click; anything else runs on the worker's own initiative.</summary>
internal enum DiagnosticTrigger
{
    Operator,
    Automatic
}

internal enum WorkerActivity
{
    Idle,
    RunningJob,
    RunningDiagnostic
}
