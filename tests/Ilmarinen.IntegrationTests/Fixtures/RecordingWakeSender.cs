using Ilmarinen.Server.Services.Wake;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.IntegrationTests.Fixtures;

/// <summary>The test server's <see cref="IWakeSender"/>: records the MAC addresses it is asked to wake, and sends nothing.</summary>
public sealed class RecordingWakeSender : IWakeSender
{
    private readonly ConcurrentQueue<string> _sent = new();

    public bool Enabled { get; set; } = true;

    /// <summary>Thrown from every send while set, as a real send that can't reach the network would.</summary>
    public Exception? Failure { get; set; }

    public IReadOnlyList<string> Sent
    {
        get { return _sent.ToList(); }
    }

    public Task SendAsync(string mac, CancellationToken ct)
    {
        if (Failure != null)
        {
            throw Failure;
        }
        _sent.Enqueue(mac);
        return Task.CompletedTask;
    }

    public void Clear()
    {
        _sent.Clear();
    }
}
