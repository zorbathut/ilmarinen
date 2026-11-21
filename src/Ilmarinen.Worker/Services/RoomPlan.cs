using System.Collections.Generic;
using System.Linq;
using System;

namespace Ilmarinen.Worker.Services;

/// <summary>A persistent workspace the worker could delete to make room: when it was last used, and the space deleting it frees (null if unmeasured).</summary>
public sealed record RoomCandidate(string Name, DateTimeOffset LastUsed, long? FreedBytes);

public static class RoomPlan
{
    /// <summary>
    /// The workspaces to delete to free <paramref name="shortfallBytes"/>, in the order to delete them: least recently used first, and only as many as the measured sizes say it takes. Chosen by measured size rather than by watching free space rise, since a filesystem may report freed space late and a deletion that seems to have freed nothing would otherwise be followed by needless more. An unmeasured workspace counts as freeing nothing, so it is deleted only if it comes before enough measured ones; if even all of them don't cover the shortfall, all are listed.
    /// </summary>
    public static IReadOnlyList<RoomCandidate> Choose(IEnumerable<RoomCandidate> candidates, long shortfallBytes)
    {
        var chosen = new List<RoomCandidate>();
        long freed = 0;
        foreach (var candidate in candidates.OrderBy(c => c.LastUsed).ThenBy(c => c.Name, StringComparer.Ordinal))
        {
            if (freed >= shortfallBytes)
            {
                break;
            }
            chosen.Add(candidate);
            freed += candidate.FreedBytes ?? 0;
        }
        return chosen;
    }
}
