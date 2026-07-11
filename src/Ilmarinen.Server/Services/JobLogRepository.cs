using Ilmarinen.Database.Entities;
using Ilmarinen.Database;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Server.Services;

public class JobLogRepository
{
    private readonly IlmarinenDbContext _db;

    public JobLogRepository(IlmarinenDbContext db)
    {
        _db = db;
    }

    public async Task SaveChunksAsync(IReadOnlyList<LogChunk> chunks)
    {
        if (chunks.Count == 0) return;

        var entities = chunks.Select(c => new JobLogChunk
        {
            Id = Guid.CreateVersion7(),
            JobId = c.JobId,
            SequenceNumber = c.SequenceNumber,
            Content = c.Content,
            Timestamp = c.Timestamp
        });

        _db.JobLogChunks.AddRange(entities);
        await _db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<LogChunkInfo>> GetChunksAsync(
        Guid jobId,
        int fromSequence = 0,
        int limit = 100)
    {
        var chunks = await _db.JobLogChunks
            .Where(c => c.JobId == jobId && c.SequenceNumber >= fromSequence)
            .OrderBy(c => c.SequenceNumber)
            .Take(limit)
            .ToListAsync();

        return chunks.Select(c => new LogChunkInfo
        {
            SequenceNumber = c.SequenceNumber,
            Content = c.Content,
            Timestamp = c.Timestamp
        }).ToList();
    }

}
