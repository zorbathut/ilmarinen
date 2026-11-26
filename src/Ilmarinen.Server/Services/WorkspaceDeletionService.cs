using Ilmarinen.Models;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.Server.Services;

public class WorkspaceDeletionService
{
    /// <summary>How long a request waits for the worker's answer.</summary>
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<DeleteWorkspaceResult>> _pending = new();
    private readonly WorkerRepository _workers;
    private readonly WorkerMessenger _messenger;

    public WorkspaceDeletionService(WorkerRepository workers, WorkerMessenger messenger)
    {
        _workers = workers;
        _messenger = messenger;
    }

    /// <summary>
    /// Asks a worker to delete one of its workspaces and waits for its answer. A name that isn't a workspace's is an Error, and a worker that isn't connected is NotFound, both without asking it.
    /// </summary>
    public async Task<DeleteWorkspaceResult> RequestAsync(Guid workerId, string name)
    {
        if (!WorkspaceConfig.IsValidName(name, out var validationError))
        {
            return new DeleteWorkspaceResult { Status = DeleteWorkspaceStatus.Error, Error = validationError };
        }

        var connectionId = _workers.FindConnectionIdByWorkerId(workerId);
        if (connectionId == null)
        {
            return new DeleteWorkspaceResult { Status = DeleteWorkspaceStatus.NotFound, Error = "Worker not found or not connected." };
        }

        var key = CreatePending(connectionId, name);
        _messenger.Send(connectionId, $"deletion of workspace {name}", client => client.DeleteWorkspace(name));
        return await WaitForResultAsync(key, AnswerTimeout);
    }

    private static string MakeKey(string connectionId, string name) => $"{connectionId}:{name}";

    private string CreatePending(string connectionId, string name)
    {
        var key = MakeKey(connectionId, name);
        var tcs = new TaskCompletionSource<DeleteWorkspaceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = tcs;
        return key;
    }

    private async Task<DeleteWorkspaceResult> WaitForResultAsync(string key, TimeSpan timeout)
    {
        if (!_pending.TryGetValue(key, out var tcs))
            return new DeleteWorkspaceResult { Status = DeleteWorkspaceStatus.Error, Error = "No pending deletion." };

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var result = await tcs.Task.WaitAsync(cts.Token);
            return result;
        }
        catch (OperationCanceledException)
        {
            return new DeleteWorkspaceResult { Status = DeleteWorkspaceStatus.Timeout, Error = "Worker did not respond in time." };
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    public void Complete(string connectionId, string name, DeleteWorkspaceResult result)
    {
        var key = MakeKey(connectionId, name);
        if (_pending.TryRemove(key, out var tcs))
        {
            tcs.TrySetResult(result);
        }
    }
}
