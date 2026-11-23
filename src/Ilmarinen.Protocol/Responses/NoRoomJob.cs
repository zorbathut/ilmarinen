namespace Ilmarinen.Protocol.Responses;

/// <summary>A queued job that available workers qualify for, but none of which has room for even after deleting every workspace it holds: it waits until one does. Its need is learned from its script's latest runs.</summary>
public record NoRoomJob
{
    public required JobInfo Job { get; init; }

    /// <summary>The disk it is expected to take, beyond the floor its worker keeps free, on the worker nearest to having room for it and by that worker's nearer way in (reusing a workspace it holds, or making it afresh).</summary>
    public required long NeededBytes { get; init; }

    /// <summary>The room that same worker has for it that way, beyond its floor, deleting what it may, which isn't enough. Zero when even that leaves the worker below its floor.</summary>
    public long? MostRoomBytes { get; init; }
}
