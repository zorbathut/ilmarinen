using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Server.Hubs;

/// <summary>
/// The server's end of the worker protocol (spec/README.md). A connection becomes the worker's current session by authenticating with an epoch its current one doesn't outrank; it then sends Resume, and only after that does anything it reports about jobs count. Calls on a connection that isn't current are answered Fenced.
/// </summary>
public class WorkerHub : Hub<IWorkerClient>
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ServerKeyService _serverKey;
    private readonly WorkerBundleService _bundles;
    private readonly UIEventService _uiEvents;
    private readonly WorkerMessenger _messenger;
    private readonly WorkerSessions _sessions;
    private readonly ILogger<WorkerHub> _logger;

    public WorkerHub(IServiceScopeFactory scopeFactory, ServerKeyService serverKey, WorkerBundleService bundles, UIEventService uiEvents, WorkerMessenger messenger, WorkerSessions sessions, ILogger<WorkerHub> logger)
    {
        _scopeFactory = scopeFactory;
        _serverKey = serverKey;
        _bundles = bundles;
        _uiEvents = uiEvents;
        _messenger = messenger;
        _sessions = sessions;
        _logger = logger;
    }

    public async Task<AuthChallenge> Connect(WorkerConnect request)
    {
        // Validate protocol compatibility early
        if (request.ProtocolHash != ProtocolVersion.Hash)
        {
            _logger.LogError(
                "Worker {WorkerId} rejected: protocol mismatch (worker: {WorkerHash}, server: {ServerHash})",
                request.WorkerId, request.ProtocolHash, ProtocolVersion.Hash);

            throw new HubException(
                $"Protocol mismatch. Worker is '{request.ProtocolHash}', server is '{ProtocolVersion.Hash}'. " +
                "Rebuild both with the same protocol definitions.");
        }

        using var scope = _scopeFactory.CreateScope();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

        var publicKey = await workers.GetWorkerPublicKeyAsync(request.WorkerId);
        if (publicKey == null)
        {
            _logger.LogWarning("Connection failed: unknown worker {WorkerId}", request.WorkerId);
            throw new HubException("Unknown worker. Register this worker via the API first.");
        }

        var serverNonce = RandomNumberGenerator.GetBytes(32);
        var challengeData = HandshakeChallenge.Build(request.WorkerId, request.InstanceId, request.ConnectionCounter, request.Nonce, serverNonce);
        var serverSignature = _serverKey.Sign(challengeData);

        _sessions.BeginHandshake(Context.ConnectionId, new PendingHandshake
        {
            WorkerId = request.WorkerId,
            InstanceId = request.InstanceId,
            ConnectionCounter = request.ConnectionCounter,
            WorkerNonce = request.Nonce,
            ServerNonce = serverNonce,
            CreatedAt = DateTime.UtcNow,
            BundleHash = request.BundleHash
        });

        _logger.LogDebug("Issued auth challenge for worker {WorkerId}", request.WorkerId);

        return new AuthChallenge
        {
            Nonce = serverNonce,
            ServerSignature = serverSignature,
            ProtocolHash = ProtocolVersion.Hash,
            CurrentBundleHash = _bundles.CurrentHash
        };
    }

    /// <summary>Verifies the worker's signature, then makes this connection its current session if the epoch allows. Fenced means a connection with a later epoch already holds it.</summary>
    public async Task<SessionAnswer> Authenticate(WorkerAuthenticate info)
    {
        if (_sessions.TakeHandshake(Context.ConnectionId) is not { } pending)
        {
            throw new HubException("No pending authentication challenge. Call Connect first.");
        }

        if (pending.CreatedAt.AddSeconds(60) < DateTime.UtcNow)
        {
            throw new HubException("Authentication challenge expired. Call Connect again.");
        }

        using var scope = _scopeFactory.CreateScope();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

        var publicKeyBytes = await workers.GetWorkerPublicKeyAsync(pending.WorkerId);
        if (publicKeyBytes == null)
        {
            throw new HubException("Unknown worker.");
        }

        using var workerEcdsa = ECDsa.Create();
        workerEcdsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);

        var challengeData = HandshakeChallenge.Build(pending.WorkerId, pending.InstanceId, pending.ConnectionCounter, pending.WorkerNonce, pending.ServerNonce);
        if (!workerEcdsa.VerifyData(challengeData, info.Signature, HashAlgorithmName.SHA256))
        {
            _logger.LogWarning("Worker {WorkerId} rejected: invalid signature", pending.WorkerId);
            throw new HubException("Invalid signature. Check that the worker key is correct.");
        }

        var session = new WorkerSession(pending.WorkerId, Context.ConnectionId, pending.InstanceId, pending.ConnectionCounter)
        {
            BundleHash = pending.BundleHash
        };
        if (info.Workspaces != null)
        {
            session.Workspaces = [.. info.Workspaces];
        }

        using (await _sessions.LockAsync(pending.WorkerId))
        {
            if (!_sessions.TryStart(session, out var superseded))
            {
                _logger.LogWarning("Worker {WorkerId} handshake on connection {Counter} of instance {InstanceId} fenced: the worker's current connection outranks it", pending.WorkerId, pending.ConnectionCounter, pending.InstanceId);
                return SessionAnswer.Fenced;
            }

            if (superseded != null && superseded.InstanceId != pending.InstanceId)
            {
                _logger.LogWarning("Worker {WorkerId} reconnected as a new process instance {InstanceId}, replacing {OldInstanceId} for good (if both are still running, two processes share its key)", pending.WorkerId, pending.InstanceId, superseded.InstanceId);
            }

            // Recorded only after the signature checks out and the connection got in, so an unauthenticated caller can't overwrite a worker's address by claiming its ID. Every reconnect authenticates afresh, so this is the sole point at which the address needs refreshing.
            var remoteAddress = Context.GetHttpContext()?.Connection.RemoteIpAddress;
            if (remoteAddress == null)
            {
                _logger.LogWarning("Worker {WorkerId} authenticated but no peer address was available", pending.WorkerId);
            }
            _sessions.SetDiagnosticOwed(pending.WorkerId, await workers.RecordConnectionAsync(pending.WorkerId, AddressFormat(remoteAddress)));
        }

        _uiEvents.NotifyWorkersChanged();
        _logger.LogInformation("Worker authenticated: {WorkerId} (connection {Counter} of instance {InstanceId})", pending.WorkerId, pending.ConnectionCounter, pending.InstanceId);
        return SessionAnswer.Ok;
    }

    /// <summary>
    /// Renders a peer address for storage. The server listens dual-stack, so IPv4 workers arrive as IPv4-mapped IPv6 (::ffff:10.0.0.5); unmap them so an operator sees the address they'd recognize.
    /// </summary>
    // Must stay static: SignalR registers public *instance* methods on a hub as client-invocable RPCs, and this hub's endpoint takes callers before they authenticate.
    public static string? AddressFormat(IPAddress? address)
    {
        if (address == null)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4().ToString();
        }

        return address.ToString();
    }

    /// <summary>
    /// The first call on an authenticated connection: the worker's state and every outcome the server hasn't acknowledged, in one step. Applies the outcomes, then reconciles what the database says the worker is running with what the worker says, then lets the connection report and take work.
    /// </summary>
    public async Task<ResumeResponse> Resume(WorkerResume resume)
    {
        if (_sessions.ByConnection(Context.ConnectionId) is not { } session)
        {
            return new ResumeResponse { Answer = SessionAnswer.Fenced };
        }

        using var scope = _scopeFactory.CreateScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<JobScheduler>();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();

        var state = resume.State;
        var abort = new List<Guid>();
        using (await _sessions.LockAsync(session.WorkerId))
        {
            if (_sessions.ByConnection(Context.ConnectionId) != session)
            {
                return new ResumeResponse { Answer = SessionAnswer.Fenced };
            }

            var recheck = _sessions.DiagnosticOwed(session.WorkerId);
            foreach (var outcome in resume.Outcomes)
            {
                recheck |= await scheduler.ApplyOutcomeAsync(session, outcome);
            }

            var reported = state.Activity == WorkerActivity.RunningJob ? state.AssignmentId : null;
            var running = await jobs.GetRunningAssignmentsAsync(session.WorkerId);
            foreach (var job in running)
            {
                if (job.AssignmentId != null && job.AssignmentId == reported)
                {
                    continue;
                }

                if (job.JobId == state.JobId)
                {
                    _logger.LogError("Worker {WorkerId} reports running job {JobId} under assignment {Reported}, but the server has it under {Assigned}", session.WorkerId, job.JobId, reported, job.AssignmentId);
                }

                // Running for the worker, yet it doesn't say so. The same process instance would have named it if it had ever arrived, so it can go back in the queue unrun; a different instance may have run part of it before dying, so it fails.
                if (job.WorkerInstanceId == session.InstanceId && job.AssignmentId is { } assignment)
                {
                    if (await jobs.RequeueAsync(job.JobId, session.WorkerId, assignment))
                    {
                        _logger.LogWarning("Worker {WorkerId} never received job {JobId}; queued it again", session.WorkerId, job.JobId);
                        _uiEvents.NotifyJobsChanged();
                    }
                }
                else if (await scheduler.FailLostAsync(job.JobId, session.WorkerId))
                {
                    _logger.LogWarning("Worker {WorkerId} came back as a new process without job {JobId}; failed it as lost", session.WorkerId, job.JobId);
                }
            }

            // The worker runs something the server no longer has running for it — cancelled or failed while it was away. It stops.
            if (reported is { } runningAssignment && !running.Any(j => j.AssignmentId == runningAssignment))
            {
                _logger.LogInformation("Worker {WorkerId} is running job {JobId}, which the server no longer has running for it; telling it to stop", session.WorkerId, state.JobId);
                abort.Add(runningAssignment);
            }

            session.State = state;
            session.Resumed = true;

            if (recheck)
            {
                _sessions.SetDiagnosticOwed(session.WorkerId, true);
                _messenger.Send(Context.ConnectionId, "host re-check", client => client.RecheckHost());
            }
        }

        _uiEvents.NotifyWorkersChanged();
        await scheduler.DispatchAsync();
        return new ResumeResponse { Answer = SessionAnswer.Ok, AbortAssignments = abort };
    }

    /// <summary>The worker's state after a change. Readiness is derived from it, so a report can free the worker for the next job.</summary>
    public async Task<SessionAnswer> ReportState(WorkerState state)
    {
        if (_sessions.ByConnection(Context.ConnectionId) is not { } session)
        {
            return SessionAnswer.Fenced;
        }

        using var scope = _scopeFactory.CreateScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<JobScheduler>();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

        using (await _sessions.LockAsync(session.WorkerId))
        {
            if (_sessions.ByConnection(Context.ConnectionId) != session)
            {
                return SessionAnswer.Fenced;
            }
            if (!session.Resumed)
            {
                return SessionAnswer.NotResumed;
            }
            if (session.State is { } latest && state.Seq <= latest.Seq)
            {
                return SessionAnswer.Ok;
            }

            session.State = state;

            // A diagnostic that has started is the one that was owed: the worker runs a fresh one for every request, even one that arrived mid-diagnostic, and readiness still waits for its verdict.
            if (state.Activity == WorkerActivity.RunningDiagnostic && _sessions.DiagnosticOwed(session.WorkerId))
            {
                await workers.SetDiagnosticOwedAsync(session.WorkerId, false);
                _sessions.SetDiagnosticOwed(session.WorkerId, false);
            }
        }

        _uiEvents.NotifyWorkersChanged();
        await scheduler.DispatchAsync();
        return SessionAnswer.Ok;
    }

    /// <summary>What became of one of the worker's assignments. Acknowledged by an Ok; anything else, and the worker keeps it for its next Resume.</summary>
    public async Task<SessionAnswer> ReportOutcome(JobOutcome outcome)
    {
        if (_sessions.ByConnection(Context.ConnectionId) is not { } session)
        {
            return SessionAnswer.Fenced;
        }

        using var scope = _scopeFactory.CreateScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<JobScheduler>();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

        using (await _sessions.LockAsync(session.WorkerId))
        {
            if (_sessions.ByConnection(Context.ConnectionId) != session)
            {
                return SessionAnswer.Fenced;
            }
            if (!session.Resumed)
            {
                return SessionAnswer.NotResumed;
            }

            // A failed job is the only evidence the server gets that a host has gone bad, and the worker can't tell a red build from a dead daemon — the diagnostic can. Only a Failed that is new asks for one: the same outcome delivered again already did.
            if (await scheduler.ApplyOutcomeAsync(session, outcome))
            {
                _logger.LogInformation("Worker {WorkerId} failed job {JobId}; re-checking its host before giving it more work", session.WorkerId, outcome.JobId);
                _sessions.SetDiagnosticOwed(session.WorkerId, true);
                _messenger.Send(Context.ConnectionId, "host re-check", client => client.RecheckHost());
            }

            workers.SetWorkspaces(Context.ConnectionId, outcome.Workspaces);
        }

        await scheduler.DispatchAsync();
        return SessionAnswer.Ok;
    }

    /// <summary>Answered Fenced on a connection that has been superseded: how an otherwise quiet worker finds out.</summary>
    public async Task<SessionAnswer> Heartbeat()
    {
        if (_sessions.ByConnection(Context.ConnectionId) is not { } session)
        {
            return SessionAnswer.Fenced;
        }

        using var scope = _scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WorkerRepository>().TouchLastSeenAsync(session.WorkerId);
        return SessionAnswer.Ok;
    }

    public async Task<SessionAnswer> ReportCommit(Guid jobId, string commitSha)
    {
        if (_sessions.ByConnection(Context.ConnectionId) is not { } session)
        {
            return SessionAnswer.Fenced;
        }

        using var scope = _scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();

        if (await jobs.GetAssignedWorkerAsync(jobId) != session.WorkerId)
        {
            _logger.LogWarning("Worker {WorkerId} reported a commit for {JobId}, which isn't assigned to it; ignoring", session.WorkerId, jobId);
            return SessionAnswer.Ok;
        }

        _logger.LogInformation("Job {JobId} resolved to commit {CommitSha}", jobId, commitSha);
        await jobs.SetCommitAsync(jobId, commitSha);
        _uiEvents.NotifyJobsChanged();
        return SessionAnswer.Ok;
    }

    /// <summary>A chunk of a job's resource metrics. Taken from the current connection like log output, and only for the worker's own job: a job reassigned since must not collect another worker's figures.</summary>
    public async Task<SessionAnswer> StreamMetrics(JobMetricsChunk chunk)
    {
        if (_sessions.ByConnection(Context.ConnectionId) is not { } session)
        {
            return SessionAnswer.Fenced;
        }

        using var scope = _scopeFactory.CreateScope();
        if (await scope.ServiceProvider.GetRequiredService<JobRepository>().GetAssignedWorkerAsync(chunk.JobId) != session.WorkerId)
        {
            _logger.LogWarning("Worker {WorkerId} sent metrics for {JobId}, which isn't assigned to it; ignoring", session.WorkerId, chunk.JobId);
            return SessionAnswer.Ok;
        }

        await scope.ServiceProvider.GetRequiredService<JobMetricsRepository>().SaveAsync(chunk);
        return SessionAnswer.Ok;
    }

    public async Task<SessionAnswer> StreamLogs(LogChunk chunk)
    {
        if (_sessions.ByConnection(Context.ConnectionId) == null)
        {
            return SessionAnswer.Fenced;
        }

        using var scope = _scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<LogStreamService>().ProcessChunkAsync(chunk);
        return SessionAnswer.Ok;
    }

    public SessionAnswer WorkspaceDeleted(string name, DeleteWorkspaceResult result)
    {
        if (_sessions.ByConnection(Context.ConnectionId) == null)
        {
            return SessionAnswer.Fenced;
        }

        using var scope = _scopeFactory.CreateScope();
        var deletionService = scope.ServiceProvider.GetRequiredService<WorkspaceDeletionService>();
        var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

        deletionService.Complete(Context.ConnectionId, name, result);
        if (result.Status == DeleteWorkspaceStatus.Success)
        {
            workers.RemoveWorkspace(Context.ConnectionId, name);
        }

        return SessionAnswer.Ok;
    }

    /// <summary>The full report, for the worker page. Readiness goes by the verdict in the worker's state, not by this.</summary>
    public SessionAnswer ReportDiagnostic(DiagnosticReport report)
    {
        if (_sessions.ByConnection(Context.ConnectionId) is not { } session)
        {
            return SessionAnswer.Fenced;
        }

        using var scope = _scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<WorkerRepository>().SetDiagnostic(session.WorkerId, report);
        _uiEvents.NotifyWorkersChanged();
        _logger.LogInformation("Worker {WorkerId} diagnostic: {Status} - {Summary}", session.WorkerId, report.Status, report.Summary);
        return SessionAnswer.Ok;
    }

    public SessionAnswer ReportWorkspaceUsage(WorkspaceUsageReport report)
    {
        if (_sessions.ByConnection(Context.ConnectionId) is not { } session)
        {
            return SessionAnswer.Fenced;
        }

        using var scope = _scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<WorkerRepository>().SetWorkspaceUsage(session.WorkerId, report);
        _uiEvents.NotifyWorkersChanged();
        return SessionAnswer.Ok;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _sessions.TakeHandshake(Context.ConnectionId);

        if (_sessions.ByConnection(Context.ConnectionId) is { } session)
        {
            using var scope = _scopeFactory.CreateScope();
            var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();
            var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
            var logService = scope.ServiceProvider.GetRequiredService<LogStreamService>();

            bool ended;
            using (await _sessions.LockAsync(session.WorkerId))
            {
                ended = _sessions.End(Context.ConnectionId) != null;
            }

            if (ended)
            {
                _logger.LogInformation("Worker disconnected: {WorkerId}", session.WorkerId);
                await workers.TouchLastSeenAsync(session.WorkerId);

                // Its jobs stay Running: the worker's next Resume says what became of them.
                foreach (var job in await jobs.GetRunningAssignmentsAsync(session.WorkerId))
                {
                    await logService.FlushJobAsync(job.JobId);
                    _logger.LogWarning("Worker {WorkerId} disconnected with running job {JobId}, awaiting reconnection", session.WorkerId, job.JobId);
                }
            }
        }

        _messenger.Forget(Context.ConnectionId);
        _uiEvents.NotifyWorkersChanged();
        await base.OnDisconnectedAsync(exception);
    }
}
