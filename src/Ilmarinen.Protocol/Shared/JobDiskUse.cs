namespace Ilmarinen.Protocol;

/// <summary>What one run worked in and how much disk it took, which is what the server learns a script's disk needs from.</summary>
public record JobDiskUse
{
    public required WorkspaceKind WorkspaceKind { get; init; }

    /// <summary>Set when <see cref="WorkspaceKind"/> is Named.</summary>
    public string? WorkspaceName { get; init; }

    /// <summary>The named workspace didn't exist on the worker before this run, which made it from scratch.</summary>
    public bool WorkspaceFresh { get; init; }

    /// <summary>How far free disk space fell below where it stood when the run began, at its lowest. Null when free space couldn't be read. Anything else writing to the same disk meanwhile counts too.</summary>
    public long? UsedBytes { get; init; }

    /// <summary>The worker stopped the run because free space fell below its floor: the run needed at least <see cref="UsedBytes"/>, and likely more.</summary>
    public bool StoppedForDisk { get; init; }
}
