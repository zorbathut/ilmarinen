using Ilmarinen.Protocol.Responses;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.Server.Services;

public class WorkspaceDeletionService
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DeleteWorkspaceResult>> _pending = new();

    private static string MakeKey(string connectionId, string name) => $"{connectionId}:{name}";

    public string CreatePending(string connectionId, string name)
    {
        var key = MakeKey(connectionId, name);
        var tcs = new TaskCompletionSource<DeleteWorkspaceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = tcs;
        return key;
    }

    public async Task<DeleteWorkspaceResult> WaitForResultAsync(string key, TimeSpan timeout)
    {
        if (!_pending.TryGetValue(key, out var tcs))
            return new DeleteWorkspaceResult { Success = false, Error = "No pending deletion." };

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var result = await tcs.Task.WaitAsync(cts.Token);
            return result;
        }
        catch (OperationCanceledException)
        {
            return new DeleteWorkspaceResult { Success = false, Error = "Worker did not respond in time." };
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
