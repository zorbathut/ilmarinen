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

    // The worker protocol version it last connected with; null until it has connected with one. Persisted so a worker that is asleep or away still counts when deciding whether the fleet has moved past a version.
    public int? ProtocolVersion { get; set; }

    public required Guid PriorityId { get; set; }
    public Priority? Priority { get; set; }

    // A host re-check or operator diagnostic was asked for and hasn't started yet; the worker takes no work until it has. Persisted so a server restart can't skip it.
    public bool DiagnosticOwed { get; set; }

    // What the operator has set this worker to do about new jobs.
    public WorkerMode Mode { get; set; }

    // The MAC address of the worker's host, in aa:bb:cc:dd:ee:ff form, for waking it with a wake-on-LAN packet; null if it can't be woken. Entered by an operator: a worker in a container can't see its host's network card.
    public string? WakeMacAddress { get; set; }
}
