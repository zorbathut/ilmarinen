using Ilmarinen.Database.Entities;
using Ilmarinen.Database;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Fixtures;

/// <summary>
/// Puts a job and some stored log chunks in the database without running anything, for tests that
/// exercise the log endpoints rather than the pipeline.
/// </summary>
public static class JobLogSeed
{
    /// <summary>
    /// Submits a job that nothing will pick up — with no worker running it sits in the queue, which
    /// is all these tests need it to do.
    /// </summary>
    public static async Task<Guid> CreateJobAsync(IntegrationTestFixture fixture)
    {
        return await fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = "https://example.invalid/repo.git",
            Ref = "master",
            ScriptPath = "pipeline.csx",
            GitTokenMode = GitTokenMode.None
        });
    }

    public static async Task InsertChunksAsync(IntegrationTestFixture fixture, Guid jobId, params (int Sequence, string Content)[] chunks)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

        foreach (var (sequence, content) in chunks)
        {
            db.JobLogChunks.Add(new JobLogChunk
            {
                Id = Guid.CreateVersion7(),
                JobId = jobId,
                SequenceNumber = sequence,
                Content = content,
                Timestamp = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// One line of the NDJSON a worker stores in a log chunk.
    /// </summary>
    public static string NdjsonLine(string type, string data)
    {
        return JsonSerializer.Serialize(new { t = type, d = data, ts = 0 }) + "\n";
    }
}
