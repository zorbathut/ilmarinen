using Ilmarinen.Database.Entities;
using Ilmarinen.Database;
using Ilmarinen.Protocol.Metrics;
using Ilmarinen.Protocol.Requests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Server.Services;

/// <summary>A job's resource-metrics stream as the worker sent it, and the per-step summary drawn from it.</summary>
public class JobMetricsRepository
{
    private readonly IlmarinenDbContext _db;
    private readonly ILogger<JobMetricsRepository> _logger;

    public JobMetricsRepository(IlmarinenDbContext db, ILogger<JobMetricsRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Stores a chunk, and the summary of any step it says ended. A repeated chunk writes the same summary again, so it does no harm.</summary>
    public async Task SaveAsync(JobMetricsChunk chunk)
    {
        _db.JobMetricsBatches.Add(new JobMetricsBatch
        {
            JobId = chunk.JobId,
            SequenceNumber = chunk.SequenceNumber,
            Content = chunk.Content,
            ReceivedAt = DateTime.UtcNow
        });

        foreach (var summary in Parse(chunk.Content).OfType<StepSummaryRecord>())
        {
            var step = new JobStep
            {
                JobId = chunk.JobId,
                Index = summary.Index,
                Name = summary.Name,
                StartedAt = summary.StartedAt.UtcDateTime,
                EndedAt = summary.Ts.UtcDateTime,
                Status = summary.Status,
                Image = summary.Image,
                ImageFromEarlierStep = summary.ImageFromEarlierStep,
                PullSeconds = summary.PullSeconds,
                CpuSeconds = summary.CpuSeconds,
                CpuThrottledSeconds = summary.CpuThrottledSeconds,
                PeakCores = summary.PeakCores,
                PeakMemBytes = summary.PeakMemBytes,
                ReadBytes = summary.ReadBytes,
                WriteBytes = summary.WriteBytes
            };
            var stored = await _db.JobSteps.FindAsync(chunk.JobId, summary.Index);
            if (stored != null)
            {
                _db.Entry(stored).CurrentValues.SetValues(step);
            }
            else
            {
                _db.JobSteps.Add(step);
            }
        }
        await _db.SaveChangesAsync();
    }

    /// <summary>The whole stream, each chunk once, in the order the worker wrote it.</summary>
    public async Task<string> GetContentAsync(Guid jobId)
    {
        var builder = new StringBuilder();
        foreach (var content in await GetChunkContentsAsync(jobId))
        {
            builder.Append(content);
        }
        return builder.ToString();
    }

    /// <summary>Every record of the job's stream. The host record heads each chunk, so it appears once per chunk.</summary>
    public async Task<IReadOnlyList<MetricsRecord>> GetRecordsAsync(Guid jobId)
    {
        return (await GetChunkContentsAsync(jobId)).SelectMany(Parse).ToList();
    }

    public async Task<IReadOnlyList<JobStep>> GetStepsAsync(Guid jobId)
    {
        return await _db.JobSteps.Where(s => s.JobId == jobId).OrderBy(s => s.Index).ToListAsync();
    }

    private async Task<IReadOnlyList<string>> GetChunkContentsAsync(Guid jobId)
    {
        var chunks = await _db.JobMetricsBatches
            .Where(b => b.JobId == jobId)
            .OrderBy(b => b.SequenceNumber)
            .ThenBy(b => b.Id)
            .Select(b => new { b.SequenceNumber, b.Content })
            .ToListAsync();

        // A chunk re-sent after a send that only appeared to fail is stored twice; keep the first.
        return chunks.GroupBy(c => c.SequenceNumber).Select(g => g.First().Content).ToList();
    }

    /// <summary>A chunk's records. A line that isn't valid JSON is a worker bug, logged and skipped so it can't hide the rest of the job's data.</summary>
    private IEnumerable<MetricsRecord> Parse(string content)
    {
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            MetricsRecord? record = null;
            try
            {
                record = MetricsRecords.Parse(line);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Skipping a metrics line that isn't a record: {Line}", line.Length > 200 ? line[..200] : line);
            }
            if (record != null)
            {
                yield return record;
            }
        }
    }
}
