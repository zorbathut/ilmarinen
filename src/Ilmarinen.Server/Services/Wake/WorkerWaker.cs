using Ilmarinen.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.Server.Services.Wake;

public enum WakeRequestResult
{
    Sent,
    NoSuchWorker,
    NoMacAddress,
    WakingOff
}

/// <summary>Wakes sleeping workers' hosts with wake-on-LAN.</summary>
public sealed class WorkerWaker
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWakeSender _sender;

    public WorkerWaker(IServiceScopeFactory scopeFactory, IWakeSender sender)
    {
        _scopeFactory = scopeFactory;
        _sender = sender;
    }

    public bool Enabled
    {
        get { return _sender.Enabled; }
    }

    /// <summary>An operator's "Wake": send the packet now. A failure to send is thrown, for the page to show.</summary>
    public async Task<WakeRequestResult> WakeNowAsync(Guid workerId, CancellationToken ct)
    {
        if (!_sender.Enabled)
        {
            return WakeRequestResult.WakingOff;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        var worker = await db.Workers.Where(w => w.Id == workerId).Select(w => new { w.WakeMacAddress }).FirstOrDefaultAsync(ct);
        if (worker == null)
        {
            return WakeRequestResult.NoSuchWorker;
        }
        if (worker.WakeMacAddress is not { } mac)
        {
            return WakeRequestResult.NoMacAddress;
        }

        await _sender.SendAsync(mac, ct);
        return WakeRequestResult.Sent;
    }
}
