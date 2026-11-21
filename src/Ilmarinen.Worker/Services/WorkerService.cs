using Docker.DotNet;
using Ilmarinen.Docker;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Ilmarinen.Worker.Metrics;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.Worker.Services;

/// <summary>
/// The worker's end of the protocol (spec/README.md). Each transport connection is its own session, numbered within this process: it authenticates, replays buffered log output, then sends Resume with the worker's state and unacknowledged outcomes. Only invocations from the current session are acted on, and only once it has resumed; a session the server answers Fenced, or that stops answering heartbeats, is abandoned for a new one.
/// </summary>
public class WorkerService : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);
    // Longer after the server refused the handshake outright: that only happens when another process holds this worker's session, and it won't change in seconds.
    private static readonly TimeSpan FencedReconnectDelay = TimeSpan.FromSeconds(30);
    // Each call of the handshake, Resume included, which waits on the server's dispatch. Without a bound, a stuck server-side call would hold the state lock indefinitely while the transport's keepalives kept the connection looking alive.
    private static readonly TimeSpan HandshakeCallTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetryPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShutdownFlushTimeout = TimeSpan.FromSeconds(10);
    // Inside logind's default InhibitDelayMaxSec of 5s, which is all the delay inhibitor buys.
    private static readonly TimeSpan SleepFlushTimeout = TimeSpan.FromSeconds(2);

    private readonly WorkerConfig _config;
    private readonly Guid _workerId;
    private readonly WorkspaceManager _workspaceManager;
    private readonly IDiskSpace _diskSpace;
    private readonly RoomMaker _roomMaker;
    private readonly IWorkerDiagnostic _diagnostic;
    private readonly SleepInhibitor _sleepInhibitor;
    private readonly UpdateSignal _updateSignal;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<WorkerService> _logger;
    private readonly MessageBuffer _messageBuffer = new();
    private readonly BufferedHubSender _sender;

    // One client for the life of the process, shared by every job's resource sampling.
    private readonly Lazy<DockerClient> _metricsDocker = new(DockerClientFactory.Create);

    // Identifies this process to the server: its connections are (instance, counter), and a later one supersedes an earlier.
    private readonly Guid _instanceId = Guid.NewGuid();
    private long _connectionCounter;
    private volatile HubSession? _session;

    // Guards _state. Held across Resume, so nothing the worker does falls between the snapshot it sends and the server's answer; invocations that arrive meanwhile wait for it.
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly WorkerProtocolState _state = new();

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _runningJobs = new();

    // Everything the worker has to finish before the process may exit: a job tearing its containers down and reporting. Entries clear themselves, so this stays the size of what is actually in flight.
    private readonly ConcurrentDictionary<long, Task> _inFlight = new();
    private long _inFlightSequence;

    // The latest full report, for the worker page; the verdict that decides readiness travels in the state.
    private volatile DiagnosticReport? _lastDiagnostic;
    private CancellationToken _stoppingToken;

    // True once an authenticated server has told us it serves a different bundle than the one we run. Only meaningful in launcher mode (BundleHash set).
    private volatile bool _updatePending;
    private int _handshakeFailures;

    // Requests for a background measurement of workspace disk usage. Capacity one and dropping extras, so requests made while one is pending coalesce into it.
    private readonly Channel<bool> _usageRequests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public WorkerService(
        WorkerConfig config,
        WorkspaceManager workspaceManager,
        IDiskSpace diskSpace,
        IWorkerDiagnostic diagnostic,
        SleepInhibitor sleepInhibitor,
        UpdateSignal updateSignal,
        IHostApplicationLifetime lifetime,
        ILogger<WorkerService> logger)
    {
        _config = config;
        _workerId = config.GetWorkerId();
        _workspaceManager = workspaceManager;
        _diskSpace = diskSpace;
        _roomMaker = new RoomMaker(config, workspaceManager, diskSpace);
        _diagnostic = diagnostic;
        _sleepInhibitor = sleepInhibitor;
        _updateSignal = updateSignal;
        _lifetime = lifetime;
        _logger = logger;
        _sender = new BufferedHubSender(AuthenticatedConnection, _messageBuffer, logger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        Directory.CreateDirectory(_config.WorkspacePath);

        // Discover Docker environment (host path for bind mounts, container ID for networking)
        var (hostPath, containerId) = await DiscoverDockerEnvironmentAsync();
        _config.HostWorkspacePath = hostPath;
        _config.WorkerContainerId = containerId;

        // The first diagnostic needs no server: it runs while the worker connects, and Resume carries whatever it has reached.
        await TryStartDiagnosticAsync(unprompted: true, DiagnosticTrigger.Automatic);

        var diagnosticRetries = RetryUnhealthyDiagnosticAsync(stoppingToken);
        var usageMeasurements = MeasureWorkspaceUsageAsync(stoppingToken);
        var diskSpaceReports = ReportDiskSpaceAsync(stoppingToken);

        HubSession? session = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            session = await OpenSessionAsync(stoppingToken);
            if (session == null)
            {
                break;
            }

            var fenced = false;
            try
            {
                if (await SyncAsync(session))
                {
                    Interlocked.Exchange(ref _handshakeFailures, 0);
                    _logger.LogInformation("Worker {WorkerId} connected and resumed (connection {Counter})", _workerId, session.Counter);
                    await CheckUpdateExitAsync();
                }
                else
                {
                    fenced = true;
                    EndSession(session, "the server fenced it");
                }
            }
            catch (Exception ex)
            {
                // Still ends the session when stopping, so the drain below runs: a handshake cut short by shutdown is not a failure of the server.
                if (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogDebug(ex, "Handshake on connection {Counter} cut short by shutdown", session.Counter);
                }
                else
                {
                    _logger.LogError(ex, "Handshake on connection {Counter} failed", session.Counter);
                    await RecordHandshakeFailureAsync();
                }
                EndSession(session, "its handshake failed");
            }

            await KeepAliveAsync(session, stoppingToken);
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await session.DisposeAsync();
            session = null;
            await DelayUnlessStopping(fenced ? FencedReconnectDelay : ReconnectDelay, stoppingToken);
        }

        await SetHoldAsync(WorkerHold.ShuttingDown, true);

        await diagnosticRetries;
        await usageMeasurements;
        await diskSpaceReports;

        // The session outlives the stop signal so the jobs it just cancelled can still report how they ended.
        await DrainInFlightWorkAsync();
        if (session != null)
        {
            await session.FlushAsync(ShutdownFlushTimeout);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // First: this signals the stopping token and waits for ExecuteAsync, which cancels the running job and waits for it to report. The connection has to outlive that, or the job it just killed would have no way to say so.
        await base.StopAsync(cancellationToken);

        if (_session is { } session)
        {
            await session.DisposeAsync();
        }
    }

    private HubConnection? AuthenticatedConnection()
    {
        return _session is { Authenticated: true, HasEnded: false } session ? session.Connection : null;
    }

    /// <summary>Starts a new transport connection as the next session of this process, retrying until one starts or the worker stops.</summary>
    private async Task<HubSession?> OpenSessionAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var connection = new HubConnectionBuilder()
                .WithUrl($"{_config.ServerUrl}/hub/workers")
                .AddJsonProtocol(options =>
                {
                    options.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
                })
                .Build();
            connection.KeepAliveInterval = TimeSpan.FromSeconds(15);
            connection.ServerTimeout = TimeSpan.FromSeconds(60);

            var session = new HubSession(connection, Interlocked.Increment(ref _connectionCounter));

            // Registered on this connection only, so an invocation is always from the session it arrived on. Posted rather than handled here: the handlers wait for the state lock, and the connection's receive loop must keep running meanwhile to deliver the answer to a Resume in progress.
            connection.On<JobAssignment>(nameof(IWorkerClient.AssignJob), assignment => Post(session, "AssignJob", () => OnJobAssignedAsync(session, assignment)));
            connection.On<string>(nameof(IWorkerClient.CancelJob), jobId => Post(session, "CancelJob", () => OnCancelJob(jobId)));
            connection.On<string>(nameof(IWorkerClient.DeleteWorkspace), name => Post(session, "DeleteWorkspace", () => OnDeleteWorkspace(session, name)));
            connection.On(nameof(IWorkerClient.RunDiagnostic), () => Post(session, "RunDiagnostic", () => OnDiagnosticRequestedAsync(session, DiagnosticTrigger.Operator)));
            connection.On(nameof(IWorkerClient.RecheckHost), () => Post(session, "RecheckHost", () => OnDiagnosticRequestedAsync(session, DiagnosticTrigger.Automatic)));
            connection.Closed += _ =>
            {
                EndSession(session, "the connection closed");
                return Task.CompletedTask;
            };
            session.Start(_logger);

            try
            {
                _logger.LogInformation("Connecting to {ServerUrl} (connection {Counter})...", _config.ServerUrl, session.Counter);
                await connection.StartAsync(stoppingToken);
                _session = session;
                return session;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Failed to connect, retrying in {Delay}", ConnectRetryDelay);
                await session.DisposeAsync();
                await DelayUnlessStopping(ConnectRetryDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                await session.DisposeAsync();
            }
        }

        return null;
    }

    private Task Post(HubSession session, string method, Func<Task> work)
    {
        if (!session.Inbox.Writer.TryWrite(work))
        {
            _logger.LogDebug("Ignoring {Method} on connection {Counter}: the session has ended", method, session.Counter);
        }
        return Task.CompletedTask;
    }

    /// <summary>Whether an invocation that arrived on this session should still be acted on: only from the current session, once it has resumed. Caller holds the state lock.</summary>
    private bool IsCurrent(HubSession session)
    {
        return session == _session && session.Resumed && !session.HasEnded;
    }

    /// <summary>
    /// Authenticates the session, replays buffered log output, then sends Resume. Returns false if the server fenced the session; throws if the handshake failed.
    /// </summary>
    private async Task<bool> SyncAsync(HubSession session)
    {
        using var call = CancellationTokenSource.CreateLinkedTokenSource(session.Ended);

        // Mutual ECDSA challenge-response, bound to this session's instance and counter.
        var workerNonce = RandomNumberGenerator.GetBytes(32);
        call.CancelAfter(HandshakeCallTimeout);
        var challenge = await session.Connection.InvokeAsync<AuthChallenge>(nameof(IWorkerHub.Connect), new WorkerConnect
        {
            WorkerId = _workerId,
            Nonce = workerNonce,
            ProtocolVersion = WorkerProtocol.Version,
            ProtocolHash = WorkerProtocol.Hash,
            InstanceId = _instanceId,
            ConnectionCounter = session.Counter,
            BundleHash = _config.BundleHash
        }, call.Token);

        var challengeData = HandshakeChallenge.Build(_workerId, _instanceId, session.Counter, workerNonce, challenge.Nonce);
        using (var serverKey = _config.GetServerPublicKey())
        {
            if (!serverKey.VerifyData(challengeData, challenge.ServerSignature, HashAlgorithmName.SHA256))
            {
                _logger.LogError("Server signature verification failed — wrong server or MITM?");
                throw new InvalidOperationException("Server identity verification failed.");
            }
        }

        // Act only when the server has an opinion (null = no bundle configured, which must never drain the fleet). Assigning rather than or-ing lets a rollback to our own bundle clear a previously pending update. Note the handshake authenticates the peer, not this field — the channel is plaintext, so a MITM can tamper with it; that only causes spurious or suppressed restarts, never code execution (the launcher verifies bundle signatures).
        if (_config.BundleHash != null && challenge.CurrentBundleHash != null)
        {
            _updatePending = challenge.CurrentBundleHash != _config.BundleHash;
        }

        byte[] signature;
        using (var workerKey = _config.GetWorkerPrivateKey())
        {
            signature = workerKey.SignData(challengeData, HashAlgorithmName.SHA256);
        }

        call.CancelAfter(HandshakeCallTimeout);
        var answer = await session.Connection.InvokeAsync<SessionAnswer>(nameof(IWorkerHub.Authenticate), new WorkerAuthenticate
        {
            Signature = signature,
            Workspaces = SafeDiscoverWorkspaces()
        }, call.Token);
        if (answer != SessionAnswer.Ok)
        {
            // Every later connection of this process outranks this one, so being fenced here means another process holds the worker's session: a second worker on the same key, or this process replaced and still running.
            _logger.LogError("The server refused connection {Counter} of process instance {InstanceId} ({Answer}): another process holds this worker's session. Is the worker key in use twice?", session.Counter, _instanceId, answer);
            return false;
        }
        session.Authenticated = true;

        // Log output first, so a job's tail is in before Resume's outcome turns the job terminal.
        var replayed = await _messageBuffer.ReplayAsync(async message =>
        {
            call.CancelAfter(HandshakeCallTimeout);
            var replayAnswer = await session.Connection.InvokeCoreAsync<SessionAnswer>(message.Method, message.Args, call.Token);
            if (replayAnswer != SessionAnswer.Ok)
            {
                _logger.LogWarning("Server answered {Answer} to a replayed {Method} on connection {Counter}", replayAnswer, message.Method, session.Counter);
                return false;
            }
            return true;
        }, _logger);
        if (!replayed)
        {
            return false;
        }

        await _stateLock.WaitAsync();
        try
        {
            // The full report, for the worker page, ahead of the state that makes the worker ready: a reader that sees it ready sees the report it got ready on.
            if (_lastDiagnostic is { } cached)
            {
                call.CancelAfter(HandshakeCallTimeout);
                var reported = await session.Connection.InvokeAsync<SessionAnswer>(nameof(IWorkerHub.ReportDiagnostic), cached, call.Token);
                if (reported != SessionAnswer.Ok)
                {
                    _logger.LogWarning("Server answered {Answer} to ReportDiagnostic on connection {Counter}", reported, session.Counter);
                    return false;
                }
            }

            var outcomes = _state.PendingOutcomes;
            call.CancelAfter(HandshakeCallTimeout);
            var response = await session.Connection.InvokeAsync<ResumeResponse>(nameof(IWorkerHub.Resume), new WorkerResume
            {
                State = _state.Snapshot(),
                Outcomes = outcomes,
                DiskSpace = ReadDiskSpace()
            }, call.Token);
            if (response.Answer != SessionAnswer.Ok)
            {
                _logger.LogWarning("Server answered {Answer} to Resume on connection {Counter}", response.Answer, session.Counter);
                return false;
            }

            _state.Acknowledge(outcomes);
            session.Resumed = true;

            if (_state.Job is { } job && response.AbortAssignments.Contains(job.AssignmentId))
            {
                _logger.LogInformation("Server no longer has job {JobId} running for this worker (cancelled or failed while it was away); stopping it", job.Id);
                CancelRunningJob(job.Id);
            }
        }
        finally
        {
            _stateLock.Release();
        }

        _usageRequests.Writer.TryWrite(true);
        return true;
    }

    /// <summary>Heartbeats the session until it ends or the worker stops. A heartbeat answered Fenced, or not answered in time, ends it: the server has moved on, or the connection has died without either end hearing.</summary>
    private async Task KeepAliveAsync(HubSession session, CancellationToken stoppingToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(session.Ended, stoppingToken);
        while (!session.HasEnded && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, wait.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(session.Ended);
            timeout.CancelAfter(HeartbeatTimeout);
            try
            {
                var answer = await session.Connection.InvokeAsync<SessionAnswer>(nameof(IWorkerHub.Heartbeat), timeout.Token);
                if (answer != SessionAnswer.Ok)
                {
                    EndSession(session, $"the server answered {answer} to a heartbeat");
                }
            }
            catch (Exception ex) when (!session.HasEnded)
            {
                _logger.LogDebug(ex, "Heartbeat on connection {Counter} failed", session.Counter);
                EndSession(session, "a heartbeat went unanswered");
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void EndSession(HubSession session, string reason)
    {
        if (session.End())
        {
            _logger.LogWarning("Abandoning connection {Counter}: {Reason}; reconnecting", session.Counter, reason);
        }
    }

    /// <summary>
    /// Queues a message on the current session, if it has resumed; sent in order, after the ones queued before it. Without one there is nothing to do: Resume carries the state and outcomes, and the rest is re-derived after it. Callers reporting a state change or outcome hold the state lock, so reports go out in the order the changes happened.
    /// </summary>
    private void Report(Func<HubSession, Task> send)
    {
        if (_session is { Resumed: true, HasEnded: false } session)
        {
            session.Outbox.Writer.TryWrite(() => send(session));
        }
    }

    /// <summary>The host is about to sleep: hold, say so, and close the connection, so the server sees the worker go now rather than at its timeout, having perhaps sent it a job in between.</summary>
    public async Task PrepareForSleepAsync()
    {
        await SetHoldAsync(WorkerHold.Sleeping, true);
        if (_session is { } session)
        {
            await session.FlushAsync(SleepFlushTimeout);
            EndSession(session, "the host is going to sleep");
        }
    }

    /// <summary>The host has woken. Whatever connection there is was opened before it slept, and is likely dead without either end knowing; rather than wait for the heartbeat to find out, start a new one.</summary>
    public async Task WokeAsync()
    {
        await SetHoldAsync(WorkerHold.Sleeping, false);
        if (_session is { } session)
        {
            EndSession(session, "the host has woken from sleep");
        }
    }

    /// <summary>Puts a hold on or takes it off, and reports the change if there is one.</summary>
    public async Task SetHoldAsync(WorkerHold hold, bool held)
    {
        await _stateLock.WaitAsync();
        try
        {
            if (_state.SetHold(hold, held))
            {
                ReportState();
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// Under the state lock: once the stop signal has come, the worker is held, and says so before anything else it reports. A job the stop cancelled ends while the connection is still up; without the hold the server would see an idle worker and hand it the next job, only to have it handed back, over and over until the connection closed.
    /// </summary>
    private void HoldIfStopping()
    {
        if (_stoppingToken.IsCancellationRequested && _state.SetHold(WorkerHold.ShuttingDown, true))
        {
            ReportState();
        }
    }

    private void ReportState()
    {
        var state = _state.Snapshot();
        Report(session => SendAsync(session, nameof(IWorkerHub.ReportState), state));
    }

    private void ReportOutcome(JobOutcome outcome)
    {
        Report(async session =>
        {
            if (!await SendAsync(session, nameof(IWorkerHub.ReportOutcome), outcome))
            {
                return;
            }

            await _stateLock.WaitAsync();
            try
            {
                _state.Acknowledge([outcome]);
            }
            finally
            {
                _stateLock.Release();
            }
            await CheckUpdateExitAsync();
        });
    }

    /// <summary>Sends one message on the session; anything but Ok abandons it, and what wasn't acknowledged goes with the next Resume.</summary>
    private async Task<bool> SendAsync(HubSession session, string method, object argument)
    {
        try
        {
            var answer = await session.Connection.InvokeAsync<SessionAnswer>(method, argument, session.Ended);
            if (answer == SessionAnswer.Ok)
            {
                return true;
            }
            EndSession(session, $"the server answered {answer} to {method}");
        }
        catch (Exception ex) when (!session.HasEnded)
        {
            _logger.LogDebug(ex, "{Method} on connection {Counter} failed", method, session.Counter);
            EndSession(session, $"{method} failed");
        }
        catch (OperationCanceledException)
        {
            // The session ended while this was in flight.
        }
        return false;
    }

    private async Task OnJobAssignedAsync(HubSession session, JobAssignment assignment)
    {
        await _stateLock.WaitAsync();
        try
        {
            if (!IsCurrent(session))
            {
                _logger.LogDebug("Ignoring job {JobId} assigned on connection {Counter}, which is no longer current", assignment.Id, session.Counter);
                return;
            }

            // The connection outlives the stop signal so a job in flight can report itself; anything assigned in that window goes back to be run elsewhere.
            HoldIfStopping();
            var handBack = _state.Assign(assignment);
            if (handBack != null)
            {
                _logger.LogWarning("Handing job {JobId} back: the worker is {Activity}{Hold}", assignment.Id, _state.Activity, _state.Hold is { } hold ? $", held ({hold})" : "");
                ReportOutcome(handBack);
                return;
            }

            ReportState();
            var cancel = new CancellationTokenSource();
            _runningJobs[assignment.Id] = cancel;
            TrackInFlight(ExecuteJobAsync(assignment, cancel));
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private Task OnCancelJob(string jobId)
    {
        _logger.LogInformation("Received cancel request for job {JobId}", jobId);
        if (!Guid.TryParse(jobId, out var id) || !CancelRunningJob(id))
        {
            _logger.LogInformation("Cancel requested for job {JobId}, which isn't running on this worker", jobId);
        }
        return Task.CompletedTask;
    }

    private bool CancelRunningJob(Guid jobId)
    {
        if (!_runningJobs.TryGetValue(jobId, out var cancel))
        {
            return false;
        }

        try
        {
            cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The job ended as the request arrived; nothing left to stop.
        }
        return true;
    }

    private Task OnDeleteWorkspace(HubSession session, string name)
    {
        _logger.LogInformation("Received workspace deletion request: {WorkspaceName}", name);
        var result = _workspaceManager.TryDelete(name);
        if (session == _session)
        {
            Report(s => SendAsync(s, nameof(IWorkerHub.WorkspaceDeleted), name, result));
        }

        // Deleting one of two workspaces that share files leaves the other as their only holder
        if (result.Status == DeleteWorkspaceStatus.Success)
        {
            _usageRequests.Writer.TryWrite(true);
        }
        return Task.CompletedTask;
    }

    private async Task<bool> SendAsync(HubSession session, string method, object first, object second)
    {
        try
        {
            var answer = await session.Connection.InvokeAsync<SessionAnswer>(method, first, second, session.Ended);
            if (answer == SessionAnswer.Ok)
            {
                return true;
            }
            EndSession(session, $"the server answered {answer} to {method}");
        }
        catch (Exception ex) when (!session.HasEnded)
        {
            _logger.LogDebug(ex, "{Method} on connection {Counter} failed", method, session.Counter);
            EndSession(session, $"{method} failed");
        }
        catch (OperationCanceledException)
        {
            // The session ended while this was in flight.
        }
        return false;
    }

    private async Task ExecuteJobAsync(JobAssignment job, CancellationTokenSource cancel)
    {
        var startTime = DateTime.UtcNow;
        var status = JobStatus.Failed;
        var logCollector = new LogCollector(job.Id, _sender, _logger);
        var jobLogger = new LoggerTee(_logger, logCollector);
        var metrics = StartMetrics(job.Id);
        await using var diskWatch = new DiskWatch(_diskSpace, _config.WorkspacePath, _config.MinFreeBytes, jobLogger);
        JobRunner? runner = null;

        try
        {
            jobLogger.LogInformation("Received job {JobId}: {RepoUrl} @ {Ref}", job.Id, job.RepoUrl, job.Ref);

            // Two tokens. CancelJob trips the first; the job runs on the second, which the host's shutdown and the disk watch trip as well — otherwise a SIGTERM mid-job leaves the step container running with nothing left to tear it down.
            using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, _stoppingToken, diskWatch.Stopping);
            try
            {
                // Keep the host from auto-suspending out from under us for as long as the job runs.
                using var inhibitor = await _sleepInhibitor.AcquireAsync($"Ilmarinen job {job.Id}");

                foreach (var (name, result) in _roomMaker.MakeRoom(job, jobLogger))
                {
                    Report(s => SendAsync(s, nameof(IWorkerHub.WorkspaceDeleted), name, result));
                }
                // Only once there is room: a worker below its floor when the job arrived would otherwise stop it before making any, and what making room freed isn't the job's to have used.
                diskWatch.Start();

                runner = new JobRunner(_config, _workspaceManager, job, _sender, jobLogger, logCollector, metrics);
                var ran = await runner.ExecuteAsync(jobCts.Token);

                // A pipeline that ran to the end reports what it actually did, even if the worker is on its way out by now. One the disk watch stopped failed, whatever a step made of being stopped.
                status = cancel.IsCancellationRequested ? JobStatus.Cancelled : diskWatch.StoppedForDisk ? JobStatus.Failed : ran;
                jobLogger.LogInformation("Job {JobId} completed with status {Status}", job.Id, status);
                await logCollector.FlushAsync();
            }
            catch (OperationCanceledException) when (jobCts.IsCancellationRequested)
            {
                // Cancelled means a person asked for it. A job the worker dropped because its host is going down, or its disk is filling, is a failure, and reporting it as one now is what keeps it from sitting Running until a reconnect that may never come. The watch has already said why it stopped the job.
                var abandoned = !cancel.IsCancellationRequested;
                if (diskWatch.StoppedForDisk)
                {
                    jobLogger.LogInformation("Job {JobId} stopped: the disk was nearly full", job.Id);
                }
                else if (abandoned)
                {
                    jobLogger.LogWarning("Job {JobId} abandoned: the worker is shutting down", job.Id);
                }
                else
                {
                    jobLogger.LogInformation("Job {JobId} was cancelled", job.Id);
                }
                status = abandoned ? JobStatus.Failed : JobStatus.Cancelled;
                await logCollector.FlushAsync();
            }
            catch (Exception ex)
            {
                status = JobStatus.Failed;
                jobLogger.LogError(ex, "Job {JobId} failed with exception", job.Id);
                await logCollector.FlushAsync();
            }
        }
        catch (Exception ex)
        {
            // Only reachable if the handling above itself threw — a log write, a flush. The job still ends, as Failed: a worker that went idle without recording how would have the server requeue a job that may have partly run.
            status = JobStatus.Failed;
            _logger.LogError(ex, "Job {JobId}'s own error handling failed; reporting it as Failed", job.Id);
        }
        finally
        {
            _runningJobs.TryRemove(job.Id, out _);
            cancel.Dispose();
            if (metrics != null)
            {
                await metrics.StopAsync();
            }
            var diskUse = new JobDiskUse
            {
                WorkspaceKind = runner?.WorkspaceKind ?? WorkspaceKind.Unknown,
                WorkspaceName = runner?.WorkspaceName,
                WorkspaceFresh = runner?.WorkspaceFresh ?? false,
                UsedBytes = diskWatch.UsedBytes,
                StoppedForDisk = diskWatch.StoppedForDisk
            };
            await FinishJobAsync(job, status, DateTime.UtcNow - startTime, diskUse);
        }
    }

    /// <summary>
    /// Starts sampling the job's resource use, unless the worker has it turned off. Best effort: a job runs, and is reported, whether or not it can be measured. Given the plain logger, for the same reason the sender is: a failure logged into the job's own log would be sent back through the same path.
    /// </summary>
    private JobMetricsSampler? StartMetrics(Guid jobId)
    {
        if (_config.MetricsInterval <= TimeSpan.Zero)
        {
            return null;
        }

        try
        {
            var sampler = new JobMetricsSampler(jobId, _config.MetricsInterval, new ProcHostSource("/proc"), new DockerContainerSource(_metricsDocker.Value, _config.WorkerContainerId), _config.WorkerContainerId, chunk => _sender.SendOrBufferAsync(nameof(IWorkerHub.StreamMetrics), chunk), _logger, TimeProvider.System);
            sampler.Start();
            return sampler;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start resource metrics for job {JobId}; it runs unmeasured", jobId);
            return null;
        }
    }

    /// <summary>Records the job's outcome and goes idle in one step under the state lock, reports both, and runs any diagnostic asked for while the job ran.</summary>
    private async Task FinishJobAsync(JobAssignment job, JobStatus status, TimeSpan duration, JobDiskUse diskUse)
    {
        var workspaces = SafeDiscoverWorkspaces();
        await _stateLock.WaitAsync();
        try
        {
            HoldIfStopping();
            var outcome = _state.FinishJob(status, duration, workspaces, diskUse);
            _logger.LogInformation("Job {JobId} finished: {Status}", job.Id, status);
            // Ahead of the outcome, which frees the worker for its next job: the server should judge that job by the space this one left.
            ReportDiskSpace();
            ReportOutcome(outcome);
            StartDiagnosticIfWanted();
            ReportState();
        }
        finally
        {
            _stateLock.Release();
        }
        _usageRequests.Writer.TryWrite(true);
    }

    private async Task OnDiagnosticRequestedAsync(HubSession session, DiagnosticTrigger trigger)
    {
        await _stateLock.WaitAsync();
        try
        {
            if (!IsCurrent(session))
            {
                _logger.LogDebug("Ignoring a diagnostic request on connection {Counter}, which is no longer current", session.Counter);
                return;
            }

            if (!_state.RequestDiagnostic())
            {
                _logger.LogInformation("Diagnostic requested while the worker is {Activity}; it runs as soon as the worker is idle", _state.Activity);
                return;
            }

            ReportState();
            StartDiagnostic(trigger);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task TryStartDiagnosticAsync(bool unprompted, DiagnosticTrigger trigger)
    {
        await _stateLock.WaitAsync();
        try
        {
            if (_state.TryStartDiagnostic(unprompted))
            {
                ReportState();
                StartDiagnostic(trigger);
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>Starts a diagnostic asked for while the worker was busy. The caller reports the state afterwards, once, so the server never sees a moment's idleness in between. Caller holds the state lock.</summary>
    private void StartDiagnosticIfWanted()
    {
        if (_state.TryStartDiagnostic(unprompted: false))
        {
            StartDiagnostic(DiagnosticTrigger.Automatic);
        }
    }

    /// <summary>Runs the diagnostic the state has just moved to. Caller holds the state lock.</summary>
    private void StartDiagnostic(DiagnosticTrigger trigger)
    {
        // The "Running" placeholder only answers an operator who just asked: shown for a diagnostic nobody asked for, it would keep painting an unhealthy worker as busy and grey out the button that reruns it.
        if (trigger == DiagnosticTrigger.Operator)
        {
            var placeholder = new DiagnosticReport
            {
                Status = DiagnosticStatus.Running,
                Summary = "Running diagnostic...",
                Steps = [],
                CheckedAt = DateTime.UtcNow
            };
            Report(s => SendAsync(s, nameof(IWorkerHub.ReportDiagnostic), placeholder));
        }

        TrackInFlight(RunDiagnosticAsync());
    }

    private async Task RunDiagnosticAsync()
    {
        _logger.LogInformation("Running diagnostic...");

        DiagnosticReport? report;
        try
        {
            report = await _diagnostic.RunAsync(_config.WorkerContainerId, _stoppingToken);
        }
        catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
        {
            // Shutting down is not a verdict on the host.
            _logger.LogInformation("Diagnostic cancelled by shutdown");
            report = null;
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

        if (report != null)
        {
            _logger.LogInformation("Diagnostic: {Status} - {Summary}", report.Status, report.Summary);
            if (report.Status == DiagnosticStatus.Degraded)
            {
                _logger.LogWarning("Worker is functional but degraded: {Summary}", report.Summary);
            }
            else if (!DiagnosticStatusPolicy.CanAcceptJobs(report.Status))
            {
                _logger.LogWarning("Worker is online but not functional: {Summary}", report.Summary);
            }
            _lastDiagnostic = report;
        }

        await _stateLock.WaitAsync();
        try
        {
            // The report first, for the same reason as in SyncAsync: the state is what makes the worker ready.
            if (report != null)
            {
                Report(s => SendAsync(s, nameof(IWorkerHub.ReportDiagnostic), report));
            }
            _state.FinishDiagnostic(report?.Status);
            if (!_stoppingToken.IsCancellationRequested)
            {
                StartDiagnosticIfWanted();
            }
            ReportState();
        }
        finally
        {
            _stateLock.Release();
        }

        await CheckUpdateExitAsync();
    }

    /// <summary>
    /// Re-runs a diagnostic that came back unhealthy. Nothing else will: the result stands for the life of the process, and only a server request asks for another — so a worker that started while DNS was down, or while the Docker daemon was restarting, stays out of the fleet until someone notices it.
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

                // Re-checked after the wait: one may have run meanwhile, or the worker may have gone offline or started draining. The count only advances when an attempt actually happens, or a long outage would back the worker off to the ceiling without ever trying.
                if (!ShouldRetryDiagnostic())
                {
                    continue;
                }

                consecutiveFailures++;
                _logger.LogInformation("Rerunning the diagnostic that left this worker out of the fleet (attempt {Attempt})", consecutiveFailures);
                await TryStartDiagnosticAsync(unprompted: true, DiagnosticTrigger.Automatic);
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
        return DiagnosticPolicy.ShouldRetry(_lastDiagnostic, _session is { Resumed: true, HasEnded: false }, _updatePending);
    }

    /// <summary>A launcher-run worker on a stale bundle exits once it has nothing left to do or to report, so the launcher can start it from the current one. The server already keeps it from being handed work.</summary>
    private async Task CheckUpdateExitAsync()
    {
        if (!_updatePending)
        {
            return;
        }

        await _stateLock.WaitAsync();
        try
        {
            if (_state.Activity == WorkerActivity.Idle && _state.PendingOutcomes.Count == 0 && _session is { Resumed: true })
            {
                RequestUpdateExit($"server bundle differs from running bundle {_config.BundleHash}");
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// In launcher mode, persistent handshake failure means the server most likely moved to an incompatible build (a protocol break prevents us from ever learning the new bundle hash the normal way), so exit and let the launcher fetch the current bundle. The launcher backs off when the manifest hash turns out unchanged, so a spurious exit here (server down, revoked key) costs nothing but a restart. A fenced handshake is not a failure: it means another connection holds the session.
    /// </summary>
    private async Task RecordHandshakeFailureAsync()
    {
        if (_config.BundleHash == null)
        {
            return;
        }

        bool busy;
        bool outcomesPending;
        await _stateLock.WaitAsync();
        try
        {
            busy = _state.Activity == WorkerActivity.RunningJob;
            outcomesPending = _state.PendingOutcomes.Count > 0;
        }
        finally
        {
            _stateLock.Release();
        }
        if (busy)
        {
            return;
        }

        var failures = Interlocked.Increment(ref _handshakeFailures);

        // An unacknowledged outcome dies with the process, so hold out far longer while one is pending — exiting would mark a finished job Failed. Not forever, though: across a genuine protocol break it is undeliverable anyway.
        var threshold = outcomesPending ? 20 : 3;
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

    private void TrackInFlight(Task work)
    {
        var key = Interlocked.Increment(ref _inFlightSequence);
        _inFlight[key] = work;
        _ = work.ContinueWith(_ => _inFlight.TryRemove(key, out _), TaskScheduler.Default);
    }

    /// <summary>
    /// Waits for work the stop signal just cancelled to tear itself down and report. Best effort: the host's shutdown timeout bounds it.
    /// </summary>
    private async Task DrainInFlightWorkAsync()
    {
        // Looped rather than a single snapshot: a job's end can start a diagnostic, which is itself in flight. It terminates because nothing new is taken on once the stopping token is set.
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

    private DiskSpaceReport? ReadDiskSpace()
    {
        return _diskSpace.Read(_config.WorkspacePath) is { } reading
            ? new DiskSpaceReport { FreeBytes = reading.FreeBytes, TotalBytes = reading.TotalBytes, MinFreeBytes = _config.MinFreeBytes }
            : null;
    }

    private void ReportDiskSpace()
    {
        if (ReadDiskSpace() is { } report)
        {
            Report(s => SendAsync(s, nameof(IWorkerHub.ReportDiskSpace), report));
        }
    }

    /// <summary>Reports free disk space every <see cref="WorkerConfig.DiskSpaceInterval"/>, so the server sees it change while the worker runs a long job or sits idle. Dropped while there is no session, like every report; Resume carries a fresh one.</summary>
    private async Task ReportDiskSpaceAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_config.DiskSpaceInterval, stoppingToken);
                ReportDiskSpace();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("Stopped reporting free disk space");
        }
    }

    /// <summary>
    /// Measures workspace disk usage whenever asked, at most once per <see cref="WorkerConfig.WorkspaceUsageInterval"/>, and reports it. Out of band because a measurement can take a while, and nothing should wait on it. Like a diagnostic report, it is derived state measured afresh after every resync, so one with no session to go on is dropped.
    /// </summary>
    private async Task MeasureWorkspaceUsageAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                await _usageRequests.Reader.ReadAsync(stoppingToken);
                try
                {
                    var stopwatch = Stopwatch.StartNew();
                    var report = await _workspaceManager.MeasureUsageAsync(stoppingToken);
                    _logger.LogInformation("Measured disk usage of {Count} workspaces and the mirrors in {ElapsedMs} ms", report.WorkspaceSizes.Count, stopwatch.ElapsedMilliseconds);
                    Report(s => SendAsync(s, nameof(IWorkerHub.ReportWorkspaceUsage), report));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Measuring workspace disk usage failed");
                }
                await Task.Delay(_config.WorkspaceUsageInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("Stopped measuring workspace disk usage");
        }
    }

    private IReadOnlyList<WorkspaceInfo> SafeDiscoverWorkspaces()
    {
        try
        {
            return _workspaceManager.DiscoverWorkspaces();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DiscoverWorkspaces failed; continuing with empty list");
            return [];
        }
    }

    private static async Task DelayUnlessStopping(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Stopping: the caller's loop condition takes it from here.
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

    /// <summary>
    /// One transport connection and what goes over it: invocations from the server, handled in order, and the worker's reports, sent in order. Ends once, for good; the next connection is a new session.
    /// </summary>
    private sealed class HubSession : IAsyncDisposable
    {
        private readonly CancellationTokenSource _ended = new();
        private Task _inbox = Task.CompletedTask;
        private Task _outbox = Task.CompletedTask;

        public HubSession(HubConnection connection, long counter)
        {
            Connection = connection;
            Counter = counter;
        }

        public HubConnection Connection { get; }
        public long Counter { get; }

        public volatile bool Authenticated;
        public volatile bool Resumed;

        public Channel<Func<Task>> Inbox { get; } = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
        public Channel<Func<Task>> Outbox { get; } = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });

        public CancellationToken Ended { get; private set; }

        public bool HasEnded
        {
            get { return _ended.IsCancellationRequested; }
        }

        public void Start(ILogger logger)
        {
            Ended = _ended.Token;
            _inbox = Drain(Inbox, logger, "an invocation from the server");
            _outbox = Drain(Outbox, logger, "a report to the server");
        }

        /// <summary>Returns true the first time, false if it had already ended.</summary>
        public bool End()
        {
            lock (_ended)
            {
                if (_ended.IsCancellationRequested)
                {
                    return false;
                }
                _ended.Cancel();
            }
            Inbox.Writer.TryComplete();
            Outbox.Writer.TryComplete();
            return true;
        }

        /// <summary>Waits, up to the timeout, for what is already queued to go out.</summary>
        public async Task FlushAsync(TimeSpan timeout)
        {
            var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!Outbox.Writer.TryWrite(() =>
                {
                    flushed.TrySetResult();
                    return Task.CompletedTask;
                }))
            {
                return;
            }
            await Task.WhenAny(flushed.Task, Task.Delay(timeout));
        }

        public async ValueTask DisposeAsync()
        {
            End();
            await Task.WhenAll(_inbox, _outbox);
            await Connection.DisposeAsync();
        }

        private async Task Drain(Channel<Func<Task>> channel, ILogger logger, string what)
        {
            await foreach (var work in channel.Reader.ReadAllAsync())
            {
                if (HasEnded)
                {
                    continue;
                }

                try
                {
                    await work();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Handling {What} on connection {Counter} failed", what, Counter);
                }
            }
        }
    }
}

/// <summary>Who asked for a diagnostic. An operator is waiting for an answer to a click; anything else runs on the worker's own initiative or the server's.</summary>
internal enum DiagnosticTrigger
{
    Operator,
    Automatic
}
