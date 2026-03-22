using Ilmarinen.Database.Entities;
using Ilmarinen.Database;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Microsoft.EntityFrameworkCore;
using NUlid;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Server.Services;

public class SubscriberRepository
{
    private readonly IlmarinenDbContext _db;

    public SubscriberRepository(IlmarinenDbContext db)
    {
        _db = db;
    }

    public async Task<SubscriberInfo> RegisterAsync(SubscriberRegistration registration)
    {
        var existing = await _db.Subscribers.FirstOrDefaultAsync(s => s.Name == registration.Name);
        if (existing != null)
        {
            existing.IsActive = true;
            existing.LastHeartbeat = DateTime.UtcNow;
            existing.HeartbeatTimeoutMinutes = registration.HeartbeatTimeoutMinutes;
            await _db.SaveChangesAsync();
            return ToInfo(existing);
        }

        var subscriber = new Subscriber
        {
            Id = Ulid.NewUlid(),
            Name = registration.Name,
            IsActive = true,
            LastHeartbeat = DateTime.UtcNow,
            HeartbeatTimeoutMinutes = registration.HeartbeatTimeoutMinutes,
            CreatedAt = DateTime.UtcNow
        };

        _db.Subscribers.Add(subscriber);
        await _db.SaveChangesAsync();
        return ToInfo(subscriber);
    }

    public async Task<SubscriberInfo?> GetAsync(Ulid id)
    {
        var subscriber = await _db.Subscribers.FirstOrDefaultAsync(s => s.Id == id);
        return subscriber != null ? ToInfo(subscriber) : null;
    }

    public async Task<bool> HeartbeatAsync(Ulid id)
    {
        var subscriber = await _db.Subscribers.FirstOrDefaultAsync(s => s.Id == id);
        if (subscriber == null) return false;

        subscriber.IsActive = true;
        subscriber.LastHeartbeat = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<List<Ulid>> GetActiveSubscriberIdsAsync()
    {
        var now = DateTime.UtcNow;
        return await _db.Subscribers
            .Where(s => s.IsActive && s.LastHeartbeat != null
                && s.LastHeartbeat.Value.AddMinutes(s.HeartbeatTimeoutMinutes) > now)
            .Select(s => s.Id)
            .ToListAsync();
    }

    public async Task DeactivateStaleAsync()
    {
        var now = DateTime.UtcNow;
        await _db.Subscribers
            .Where(s => s.IsActive && s.LastHeartbeat != null
                && s.LastHeartbeat.Value.AddMinutes(s.HeartbeatTimeoutMinutes) <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
    }

    private static SubscriberInfo ToInfo(Subscriber s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        IsActive = s.IsActive,
        LastHeartbeat = s.LastHeartbeat
    };
}
