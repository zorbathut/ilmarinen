using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Hubs;
using Ilmarinen.Server.Services;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

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

    public IWorkerClient Client(string connectionId)
    {
        return SentTo(connectionId);
    }

    public RecordingWorkerClient SentTo(string connectionId)
    {
        return _clients.GetOrAdd(connectionId, id => new RecordingWorkerClient(_hub.Client(id)));
    }
}

/// <summary>Written only by the connection's drain task; read it after <see cref="WorkerMessenger.DrainedAsync"/>.</summary>
public sealed class RecordingWorkerClient : IWorkerClient
{
    private readonly IWorkerClient _inner;

    public RecordingWorkerClient(IWorkerClient inner)
    {
        _inner = inner;
    }

    /// <summary>Every message, in the order it was sent.</summary>
    public List<string> Messages { get; } = [];
    public List<JobAssignment> Assignments { get; } = [];
    public List<string> CancelledJobs { get; } = [];
    public int DiagnosticRequests { get; private set; }
    public int OperatorDiagnosticRequests { get; private set; }

    public Task AssignJob(JobAssignment assignment)
    {
        Messages.Add($"AssignJob {assignment.Id}");
        Assignments.Add(assignment);
        return _inner.AssignJob(assignment);
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
