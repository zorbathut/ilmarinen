using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
public class WorkerMessengerTests
{
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(10);

    [Test]
    public async Task Send_ToOneConnection_ArrivesInTheOrderQueued()
    {
        var clients = new ScriptedClients();
        var messenger = new WorkerMessenger(clients, NullLogger<WorkerMessenger>.Instance);

        for (var i = 0; i < 50; i++)
        {
            var workspace = $"ws-{i}";
            messenger.Send("conn", workspace, client => client.DeleteWorkspace(workspace));
        }
        await messenger.DrainedAsync("conn").WaitAsync(Cap);

        Assert.That(clients.For("conn").Received, Is.EqualTo(Enumerable(50)));
    }

    [Test]
    public async Task Send_ToAStalledConnection_HoldsUpNeitherTheCallerNorOtherConnections()
    {
        var clients = new ScriptedClients();
        var stall = new TaskCompletionSource();
        clients.For("stalled").Stall = stall.Task;
        var messenger = new WorkerMessenger(clients, NullLogger<WorkerMessenger>.Instance);

        messenger.Send("stalled", "first", client => client.DeleteWorkspace("first"));
        messenger.Send("stalled", "second", client => client.DeleteWorkspace("second"));
        messenger.Send("live", "other", client => client.DeleteWorkspace("other"));
        await messenger.DrainedAsync("live").WaitAsync(Cap);
        await clients.For("stalled").FirstReceived.Task.WaitAsync(Cap);

        Assert.That(clients.For("live").Received, Is.EqualTo(new[] { "other" }));
        Assert.That(clients.For("stalled").Received, Is.EqualTo(new[] { "first" }), "the stalled send is still in progress and nothing behind it has overtaken it");

        stall.SetResult();
        await messenger.DrainedAsync("stalled").WaitAsync(Cap);
        Assert.That(clients.For("stalled").Received, Is.EqualTo(new[] { "first", "second" }));
    }

    [Test]
    public async Task Send_ThatFails_DoesNotStopTheMessagesBehindIt()
    {
        var clients = new ScriptedClients();
        clients.For("conn").FailOn = "bad";
        var messenger = new WorkerMessenger(clients, NullLogger<WorkerMessenger>.Instance);

        messenger.Send("conn", "bad", client => client.DeleteWorkspace("bad"));
        messenger.Send("conn", "good", client => client.DeleteWorkspace("good"));
        await messenger.DrainedAsync("conn").WaitAsync(Cap);

        Assert.That(clients.For("conn").Received, Is.EqualTo(new[] { "bad", "good" }));
    }

    private static IEnumerable<string> Enumerable(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return $"ws-{i}";
        }
    }

    private sealed class ScriptedClients : IWorkerClients
    {
        private readonly ConcurrentDictionary<string, ScriptedClient> _clients = new();

        public ScriptedClient For(string connectionId)
        {
            return _clients.GetOrAdd(connectionId, _ => new ScriptedClient());
        }

        public IWorkerClient Client(string connectionId)
        {
            return For(connectionId);
        }
    }

    private sealed class ScriptedClient : IWorkerClient
    {
        public ConcurrentQueue<string> Received { get; } = new();
        public Task Stall { get; set; } = Task.CompletedTask;
        public string? FailOn { get; set; }
        public TaskCompletionSource FirstReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task DeleteWorkspace(string workspaceName)
        {
            Received.Enqueue(workspaceName);
            FirstReceived.TrySetResult();
            await Stall;
            if (workspaceName == FailOn)
            {
                throw new InvalidOperationException("send failed");
            }
        }

        public Task AssignJob(JobAssignment assignment)
        {
            throw new NotSupportedException();
        }

        public Task CancelJob(string jobId)
        {
            throw new NotSupportedException();
        }

        public Task RunDiagnostic()
        {
            throw new NotSupportedException();
        }

        public Task RecheckHost()
        {
            throw new NotSupportedException();
        }
    }
}
