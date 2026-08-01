using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Text.Json;
using System;

namespace Ilmarinen.Server;

/// <summary>
/// One piece of output from a job: "o" for stdout, "e" for stderr, "m" for pipeline metadata.
/// </summary>
public record LogEntry
{
    public required string Type { get; init; }
    public required string Data { get; init; }
}

/// <summary>
/// Decodes the NDJSON stored in a job's log chunks — one {"t":type,"d":data,"ts":millis} object per
/// line — into the output entries it carries. Lines of any other type are not output and are
/// dropped.
///
/// wwwroot/log-viewer.js decodes the same format in the browser, for chunks the feed serves
/// verbatim; the two have to agree on which types count as output.
/// </summary>
public static class LogChunkParser
{
    public static IEnumerable<LogEntry> ParseEntries(string chunkContent)
    {
        foreach (var line in chunkContent.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var entry = ParseLine(line);
            if (entry != null)
            {
                yield return entry;
            }
        }
    }

    private static LogEntry? ParseLine(string line)
    {
        StoredLine? stored;

        try
        {
            stored = JsonSerializer.Deserialize<StoredLine>(line);
        }
        catch (JsonException)
        {
            // Chunk boundaries are line-aligned, so a line that won't parse means the stored content is corrupt. Dropping it beats failing the whole log for a reader who can't recover either way.
            return null;
        }

        if (stored?.Type is not ("o" or "e" or "m") || stored.Data == null)
        {
            return null;
        }

        return new LogEntry
        {
            Type = stored.Type,
            Data = stored.Data
        };
    }

    private record StoredLine
    {
        [JsonPropertyName("t")]
        public string? Type { get; init; }

        [JsonPropertyName("d")]
        public string? Data { get; init; }
    }
}
