using System;

namespace Ilmarinen.Protocol.Requests;

/// <summary>POST /api/jobs/{id}/punt: stop the job's run and put it back in the queue.</summary>
public record PuntRequest
{
    /// <summary>When the run to punt started, as the caller saw it: a request for a run that has since ended is refused rather than punting the next one.</summary>
    public required DateTime StartedAt { get; init; }

    /// <summary>Punt even if the job would come straight back to the same worker.</summary>
    public bool Force { get; init; }
}
