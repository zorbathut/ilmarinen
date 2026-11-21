namespace Ilmarinen.Protocol.Responses;

/// <summary>A queued job that available workers qualify for, but none of which has room for even after deleting every workspace it holds: it waits until one does. Its need is learned from its script's latest runs.</summary>
public record NoRoomJob
{
    public required JobInfo Job { get; init; }

    /// <summary>The disk it is expected to take, beyond the floor its worker keeps free.</summary>
    public required long NeededBytes { get; init; }

    /// <summary>The most any of those workers could give it, beyond its floor; null when none has reported its free space.</summary>
    public long? MostRoomBytes { get; init; }
}
