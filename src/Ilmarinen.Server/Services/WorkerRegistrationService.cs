using Ilmarinen.Database.Entities;
using Ilmarinen.Database;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Server.Services;

public class WorkerRegistrationService
{
    private readonly IlmarinenDbContext _db;
    private readonly ServerKeyService _serverKey;

    public WorkerRegistrationService(IlmarinenDbContext db, ServerKeyService serverKey)
    {
        _db = db;
        _serverKey = serverKey;
    }

    public async Task<WorkerRegistrationResult> RegisterWorkerAsync(string name)
    {
        if (!_serverKey.IsEnabled)
        {
            throw new ConfigurationException(
                "Cannot register workers: ILMARINEN_SERVER_KEY is not configured. " +
                "Generate a key with: openssl rand -base64 32");
        }

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Worker name is required.", nameof(name));

        var exists = await _db.Workers.AnyAsync(w => w.Name == name);
        if (exists)
            throw new ArgumentException($"A worker named '{name}' already exists.");

        // Generate ECDSA keypair for the worker
        using var workerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var workerParams = workerKey.ExportParameters(true);
        var workerPublicKeyBytes = workerKey.ExportSubjectPublicKeyInfo();
        var workerPrivateScalar = workerParams.D!;

        var workerId = Guid.CreateVersion7();
        var now = DateTime.UtcNow;

        var worker = new Worker
        {
            Id = workerId,
            Name = name,
            PublicKey = workerPublicKeyBytes,
            RegisteredAt = now,
            LastSeen = now
        };
        _db.Workers.Add(worker);
        await _db.SaveChangesAsync();

        // Build combined key: {name}:{ulidBase64}:{workerPrivBase64}:{serverPubBase64}
        var workerPrivBase64 = Convert.ToBase64String(workerPrivateScalar);
        var serverPubBase64 = _serverKey.GetPublicKeyBase64();
        var combinedKey = $"{name}:{workerId}:{workerPrivBase64}:{serverPubBase64}";

        return new WorkerRegistrationResult
        {
            WorkerId = workerId,
            WorkerKey = combinedKey
        };
    }

    public async Task RevokeWorkerAsync(Guid workerId)
    {
        var worker = await _db.Workers.FirstOrDefaultAsync(w => w.Id == workerId);
        if (worker == null)
            throw new KeyNotFoundException($"Worker {workerId} not found.");

        _db.Workers.Remove(worker);
        await _db.SaveChangesAsync();
    }
}

public record WorkerRegistrationResult
{
    public required Guid WorkerId { get; init; }
    public required string WorkerKey { get; init; }
}
