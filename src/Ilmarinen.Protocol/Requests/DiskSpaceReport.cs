namespace Ilmarinen.Protocol.Requests;

/// <summary>
/// Worker → Server: free space on the filesystem holding the worker's workspaces. Cheap to read, so sent with Resume, before each job's outcome, and every half minute besides.
/// </summary>
public record DiskSpaceReport
{
    public required long FreeBytes { get; init; }
    public required long TotalBytes { get; init; }

    /// <summary>The floor the worker keeps free: it stops a job that would take free space below it.</summary>
    public required long MinFreeBytes { get; init; }
}
