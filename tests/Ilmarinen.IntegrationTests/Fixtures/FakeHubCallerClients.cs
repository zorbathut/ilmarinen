using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Fixtures;

/// <summary>
/// Records what a hub method sends back to the connection that called it, for tests that invoke hub methods directly
/// through <see cref="FakeHubCallerContext"/>. Only Caller is implemented: every other audience throws, so a test that
/// starts depending on one fails loudly instead of silently recording nothing.
/// </summary>
public sealed class FakeHubCallerClients : IHubCallerClients<IWorkerClient>
{
    public RecordingWorkerClient CallerClient { get; } = new();

    public IWorkerClient Caller => CallerClient;

    public IWorkerClient All => throw new NotSupportedException();
    public IWorkerClient Others => throw new NotSupportedException();
    public IWorkerClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
    public IWorkerClient Client(string connectionId) => throw new NotSupportedException();
    public IWorkerClient Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();
    public IWorkerClient Group(string groupName) => throw new NotSupportedException();
    public IWorkerClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
    public IWorkerClient Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
    public IWorkerClient OthersInGroup(string groupName) => throw new NotSupportedException();
    public IWorkerClient User(string userId) => throw new NotSupportedException();
    public IWorkerClient Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();

    public sealed class RecordingWorkerClient : IWorkerClient
    {
        public List<JobAssignment> Assignments { get; } = [];

        public Task AssignJob(JobAssignment assignment)
        {
            Assignments.Add(assignment);
            return Task.CompletedTask;
        }

        public Task CancelJob(string jobId)
        {
            return Task.CompletedTask;
        }

        public Task DeleteWorkspace(string workspaceName)
        {
            return Task.CompletedTask;
        }

        public Task RunDiagnostic()
        {
            return Task.CompletedTask;
        }
    }
}
