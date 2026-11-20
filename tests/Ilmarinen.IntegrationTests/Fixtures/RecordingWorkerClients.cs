using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Hubs;
using Ilmarinen.Server.Services;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Fixtures;

/// <summary>
/// The test server's <see cref="IWorkerClients"/>: records what reaches each connection, then forwards it, so real workers still get their messages while directed tests read what the server sent a connection that only exists in the test.
/// </summary>
public sealed class RecordingWorkerClients : IWorkerClients
{
    private readonly HubWorkerClients _hub;
    private readonly ConcurrentDictionary<string, RecordingWorkerClient> _clients = new();

    public RecordingWorkerClients(IHubContext<WorkerHub, IWorkerClient> hubContext)
    {
        _hub = new HubWorkerClients(hubContext);
    }

    /// <summary>How long each job assignment waits before it goes out (and holds up what is queued behind it), to force the orderings where it arrives late.</summary>
    public TimeSpan AssignmentDelay { get; set; } = TimeSpan.Zero;

    public IWorkerClient Client(string connectionId)
    {
        return SentTo(connectionId);
    }

    /// <summary>How many times the job has been assigned, over every connection.</summary>
    public int AssignmentsOf(Guid jobId)
    {
        return _clients.Values.Sum(c => c.Assignments.Count(a => a.Id == jobId));
    }

    public RecordingWorkerClient SentTo(string connectionId)
    {
        return _clients.GetOrAdd(connectionId, id => new RecordingWorkerClient(_hub.Client(id), this));
    }
}

/// <summary>Written only by the connection's drain task; read it after <see cref="WorkerMessenger.DrainedAsync"/>.</summary>
public sealed class RecordingWorkerClient : IWorkerClient
{
    private readonly IWorkerClient _inner;
    private readonly RecordingWorkerClients _owner;

    public RecordingWorkerClient(IWorkerClient inner, RecordingWorkerClients owner)
    {
        _inner = inner;
        _owner = owner;
    }

    /// <summary>Every message, in the order it was sent.</summary>
    public List<string> Messages { get; } = [];
    public List<JobAssignment> Assignments { get; } = [];
    public List<string> CancelledJobs { get; } = [];
    public int DiagnosticRequests { get; private set; }
    public int OperatorDiagnosticRequests { get; private set; }

    public async Task AssignJob(JobAssignment assignment)
    {
        Messages.Add($"AssignJob {assignment.Id}");
        Assignments.Add(assignment);
        await Task.Delay(_owner.AssignmentDelay);
        await _inner.AssignJob(assignment);
    }

    public Task CancelJob(string jobId)
    {
        Messages.Add($"CancelJob {jobId}");
        CancelledJobs.Add(jobId);
        return _inner.CancelJob(jobId);
    }

    public Task DeleteWorkspace(string workspaceName)
    {
        Messages.Add($"DeleteWorkspace {workspaceName}");
        return _inner.DeleteWorkspace(workspaceName);
    }

    public Task RunDiagnostic()
    {
        Messages.Add("RunDiagnostic");
        OperatorDiagnosticRequests++;
        return _inner.RunDiagnostic();
    }

    public Task RecheckHost()
    {
        Messages.Add("RecheckHost");
        DiagnosticRequests++;
        return _inner.RecheckHost();
    }
}
