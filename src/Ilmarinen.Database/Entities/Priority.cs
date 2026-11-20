using Ilmarinen.Protocol;
using System;

namespace Ilmarinen.Database.Entities;

/// <summary>
/// One entry in an operator's list of worker or job priorities. The foreign keys that point here don't check the kind: that a worker's priority, or a minimum, is a worker priority and a job's priority a job one is up to the code that resolves them (PriorityRepository).
/// </summary>
public class Priority
{
    public required Guid Id { get; set; }
    public required PriorityKind Kind { get; set; }

    // Unique within the kind regardless of case: the column's collation compares case-insensitively.
    public required string Name { get; set; }

    // Higher is higher priority. Only the order means anything; the values needn't be contiguous.
    public required int Rank { get; set; }

    // What a new worker, or a job given no priority, gets. Exactly one per kind.
    public bool IsDefault { get; set; }
}
