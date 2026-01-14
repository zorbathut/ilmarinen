namespace Ilmarinen.Protocol.Responses;

/// <summary>
/// Server → CLI/UI: Paginated historical log retrieval via HTTP GET /api/jobs/{id}/logs
/// </summary>
public record LogPage
{
    public required IReadOnlyList<LogChunkInfo> Chunks { get; init; }
    public required int TotalChunks { get; init; }
    public required bool HasMore { get; init; }
}

public record LogChunkInfo
{
    public required int SequenceNumber { get; init; }
    public required string Content { get; init; }
    public required DateTime Timestamp { get; init; }
}
