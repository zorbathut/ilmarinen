using Ilmarinen.Protocol;
using System;

namespace Ilmarinen.Database.Entities;

public class Worker
{
    // Set at registration, immutable
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public required byte[] PublicKey { get; set; }
    public DateTime RegisteredAt { get; set; }

    public DateTime LastSeen { get; set; }

    // Null until the worker's first successful authentication
    public string? LastIpAddress { get; set; }

    public WorkerPriority Priority { get; set; } = WorkerPriority.Medium;

    // A host re-check or operator diagnostic was asked for and hasn't started yet; the worker takes no work until it has. Persisted so a server restart can't skip it.
    public bool DiagnosticOwed { get; set; }

    // The operator has held this worker back from new jobs; what it is running carries on.
    public bool Paused { get; set; }
}
