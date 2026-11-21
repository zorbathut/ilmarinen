using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Hubs;
using Ilmarinen.Server.Services;
using Ilmarinen.Worker;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Fixtures;

/// <summary>
/// A registered worker played by hand against the hub: real signed handshakes, then whatever states and outcomes a test needs it to report, in whatever order. For the server's side of the protocol, whose interleavings a real worker reaches only slowly and by luck.
/// </summary>
public sealed class DirectedWorker
{
    private readonly IntegrationTestFixture _fixture;
    private readonly string _key;
    private long _counter;
    private long _seq;

    private DirectedWorker(IntegrationTestFixture fixture, Guid workerId, string key)
    {
        _fixture = fixture;
        WorkerId = workerId;
        _key = key;
    }

    public Guid WorkerId { get; }

    /// <summary>The process this worker currently is; <see cref="Restart"/> makes a new one.</summary>
    public Guid InstanceId { get; private set; } = Guid.NewGuid();

    public string ConnectionId { get; private set; } = "";
    public WorkerHub Hub { get; private set; } = null!;

    public static async Task<DirectedWorker> RegisterAsync(IntegrationTestFixture fixture, string name)
    {
        using var scope = fixture.Services.CreateScope();
        var registration = await scope.ServiceProvider.GetRequiredService<WorkerRegistrationService>().RegisterWorkerAsync(name);
        return new DirectedWorker(fixture, registration.WorkerId, registration.WorkerKey);
    }

    /// <summary>Registers a worker, connects it and resumes it idle and healthy: one the server can hand a job to.</summary>
    public static async Task<DirectedWorker> ReadyAsync(IntegrationTestFixture fixture, string name, string connectionId)
    {
        var worker = await RegisterAsync(fixture, name);
        await worker.ConnectAsync(connectionId);
        await worker.ResumeAsync(worker.Idle());
        return worker;
    }

    /// <summary>A new process: a new instance, counting its connections from 1 again.</summary>
    public void Restart()
    {
        InstanceId = Guid.NewGuid();
        _counter = 0;
    }

    /// <summary>Plays a particular process instance from here on, as an earlier process of this worker still running would.</summary>
    public void UseInstance(Guid instanceId)
    {
        InstanceId = instanceId;
    }

    /// <summary>Handshakes on a new connection, the next in this process's count.</summary>
    /// <param name="workspaces">The persistent workspaces the worker says it holds.</param>
    public Task<SessionAnswer> ConnectAsync(string connectionId, string? bundleHash = null, IReadOnlyList<WorkspaceInfo>? workspaces = null)
    {
        return ConnectAsCounterAsync(connectionId, ++_counter, bundleHash, workspaces);
    }

    /// <summary>Handshakes on a connection with a chosen counter: a late handshake from an earlier connection, say.</summary>
    public async Task<SessionAnswer> ConnectAsCounterAsync(string connectionId, long counter, string? bundleHash = null, IReadOnlyList<WorkspaceInfo>? workspaces = null)
    {
        ConnectionId = connectionId;
        Hub = _fixture.CreateHub(connectionId);

        var nonce = RandomNumberGenerator.GetBytes(32);
        var challenge = await Hub.Connect(new WorkerConnect
        {
            WorkerId = WorkerId,
            ProtocolHash = ProtocolVersion.Hash,
            Nonce = nonce,
            InstanceId = InstanceId,
            ConnectionCounter = counter,
            BundleHash = bundleHash
        });

        using var key = new WorkerConfig { WorkerKey = _key, ServerUrl = "unused" }.GetWorkerPrivateKey();
        var signature = key.SignData(HandshakeChallenge.Build(WorkerId, InstanceId, counter, nonce, challenge.Nonce), HashAlgorithmName.SHA256);
        return await Hub.Authenticate(new WorkerAuthenticate { Signature = signature, Workspaces = workspaces });
    }

    public Task<ResumeResponse> ResumeAsync(WorkerState state, params JobOutcome[] outcomes)
    {
        return Hub.Resume(new WorkerResume { State = state, Outcomes = outcomes });
    }

    public Task<SessionAnswer> ReportAsync(WorkerState state)
    {
        return Hub.ReportState(state);
    }

    public Task<SessionAnswer> ReportAsync(JobOutcome outcome)
    {
        return Hub.ReportOutcome(outcome);
    }

    public WorkerState Idle(DiagnosticStatus diagnostic = DiagnosticStatus.Healthy)
    {
        return new WorkerState { Seq = ++_seq, Activity = WorkerActivity.Idle, DiagnosticStatus = diagnostic };
    }

    public WorkerState Running(Guid jobId, Guid assignmentId)
    {
        return new WorkerState { Seq = ++_seq, Activity = WorkerActivity.RunningJob, JobId = jobId, AssignmentId = assignmentId, DiagnosticStatus = DiagnosticStatus.Healthy };
    }

    public WorkerState Diagnosing(DiagnosticStatus? previous = DiagnosticStatus.Healthy)
    {
        return new WorkerState { Seq = ++_seq, Activity = WorkerActivity.RunningDiagnostic, DiagnosticStatus = previous };
    }

    public static JobOutcome Completed(Guid jobId, Guid assignmentId, JobStatus status)
    {
        return new JobOutcome { JobId = jobId, AssignmentId = assignmentId, Kind = JobOutcomeKind.Completed, Status = status, Duration = TimeSpan.FromSeconds(1) };
    }

    public static JobOutcome HandBack(Guid jobId, Guid assignmentId)
    {
        return new JobOutcome { JobId = jobId, AssignmentId = assignmentId, Kind = JobOutcomeKind.Rejected };
    }

    public async Task<bool> IsReadyAsync()
    {
        var view = await _fixture.Services.GetRequiredService<WorkerRepository>().GetByIdAsync(WorkerId);
        return view!.IsReady;
    }

    /// <summary>What the server has sent this worker's current connection.</summary>
    public Task<RecordingWorkerClient> SentAsync()
    {
        return _fixture.SentToAsync(ConnectionId);
    }
}
