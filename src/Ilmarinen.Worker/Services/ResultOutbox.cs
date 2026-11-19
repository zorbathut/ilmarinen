using Ilmarinen.Protocol.Responses;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Worker.Services;

/// <summary>
/// Job results waiting for the server to acknowledge them. Unlike the <see cref="MessageBuffer"/>, which may drop old log output to stay bounded, nothing here is ever dropped: a lost result leaves its job Running on the server until something fails it as lost. A result stays until a send of it has returned.
/// </summary>
internal sealed class ResultOutbox
{
    private readonly object _lock = new();
    private readonly List<PendingResult> _pending = [];

    public bool IsEmpty
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count == 0;
            }
        }
    }

    /// <summary>Keeps a result until <see cref="Acknowledge"/> is called with the returned entry.</summary>
    public PendingResult Add(Guid jobId, JobResult result)
    {
        var entry = new PendingResult(jobId, result);
        lock (_lock)
        {
            _pending.Add(entry);
        }
        return entry;
    }

    public void Acknowledge(PendingResult entry)
    {
        lock (_lock)
        {
            _pending.Remove(entry);
        }
    }

    /// <summary>Sends every pending result, oldest first, removing each once its send returns. A failed send stops the replay and rethrows, leaving it and everything after it in place. Single caller at a time.</summary>
    public async Task ReplayAsync(Func<PendingResult, Task> send)
    {
        PendingResult[] snapshot;
        lock (_lock)
        {
            snapshot = _pending.ToArray();
        }

        foreach (var entry in snapshot)
        {
            await send(entry);
            Acknowledge(entry);
        }
    }

    /// <summary>A reference type on purpose: two results for the same job (a rejected redelivery of the running one, say) are separate entries.</summary>
    internal sealed class PendingResult
    {
        public PendingResult(Guid jobId, JobResult result)
        {
            JobId = jobId;
            Result = result;
        }

        public Guid JobId { get; }
        public JobResult Result { get; }
    }
}
