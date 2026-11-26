using Ilmarinen.Models;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Microsoft.Extensions.Logging;
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
    private readonly WorkerSessions _sessions;
    private readonly ILogger<WorkspaceDeletionService> _logger;

    public WorkspaceDeletionService(WorkerRepository workers, WorkerMessenger messenger, WorkerSessions sessions, ILogger<WorkspaceDeletionService> logger)
    {
        _workers = workers;
        _messenger = messenger;
        _sessions = sessions;
        _logger = logger;
    }

    /// <summary>
    /// Asks a worker to delete one of its workspaces and waits for its answer. A name that isn't a workspace's is an Error, and a worker that isn't connected is NotFound, both without asking it. InUse means the running job has it, and the delete is queued until the job lets it go (<see cref="SendFreed"/>).
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
        var result = await WaitForResultAsync(key, AnswerTimeout);
        if (result.Status == DeleteWorkspaceStatus.InUse)
        {
            using (await _sessions.LockAsync(workerId))
            {
                _workers.QueueDelete(workerId, name);
                SendFreed(workerId);
            }
        }
        return result;
    }

    /// <summary>
    /// Sends the queued deletes, not yet sent, of every workspace the worker's latest state no longer has its job holding. Called under the worker's session lock with each new state, before any dispatch, so a delete reaches the worker ahead of the next assignment, which could take the workspace again. A refusal can arrive after the state that frees the workspace, so queueing calls it too.
    /// </summary>
    public void SendFreed(Guid workerId)
    {
        if (_sessions.ByWorker(workerId) is not { Resumed: true, State: { } state } session)
        {
            return;
        }
        foreach (var name in _workers.TakeQueuedDeletesToSend(workerId, state.Workspace))
        {
            _logger.LogInformation("Worker {WorkerId}'s state no longer has its job holding workspace {WorkspaceName}; sending the delete asked for while it did", workerId, name);
            _messenger.Send(session.ConnectionId, $"queued deletion of workspace {name}", client => client.DeleteWorkspace(name));
        }
    }

    /// <summary>
    /// Settles a queued delete by the worker's answer, whichever request it answers. Refused again, the job has the workspace once more, and it waits again; gone, it is done; failed, it is reported and dropped. Caller holds the worker's session lock.
    /// </summary>
    public void Answered(Guid workerId, string name, DeleteWorkspaceResult result)
    {
        if (!_workers.IsDeleteQueued(workerId, name))
        {
            return;
        }
        switch (result.Status)
        {
            case DeleteWorkspaceStatus.InUse:
                _workers.QueueDelete(workerId, name);
                SendFreed(workerId);
                break;
            case DeleteWorkspaceStatus.Success:
            case DeleteWorkspaceStatus.NotFound:
                _workers.UnqueueDelete(workerId, name);
                break;
            default:
                _logger.LogWarning("Worker {WorkerId} failed to delete workspace {WorkspaceName}, which was queued for when its job ended: {Status} {Error}", workerId, name, result.Status, result.Error);
                _workers.UnqueueDelete(workerId, name);
                break;
        }
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
