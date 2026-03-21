using Ilmarinen.Database;
using Ilmarinen.Protocol;
using Microsoft.EntityFrameworkCore;
using NUlid;

namespace Ilmarinen.Server.Services;

public class DashboardService
{
    private readonly IlmarinenDbContext _db;

    public DashboardService(IlmarinenDbContext db)
    {
        _db = db;
    }

    public async Task<DashboardStats> GetStatsAsync()
    {
        var jobs = await _db.Jobs.ToListAsync();
        var workers = await _db.Workers.ToListAsync();

        return new DashboardStats
        {
            TotalJobs = jobs.Count,
            RunningJobs = jobs.Count(j => j.Status == JobStatus.Running),
            QueuedJobs = jobs.Count(j => j.Status == JobStatus.Queued),
            SuccessfulJobs = jobs.Count(j => j.Status == JobStatus.Success),
            FailedJobs = jobs.Count(j => j.Status == JobStatus.Failed),
            TotalWorkers = workers.Count,
            ConnectedWorkers = workers.Count(w => w.IsConnected)
        };
    }

    public async Task<IReadOnlyList<RecentJob>> GetRecentJobsAsync(int count = 10)
    {
        var jobs = await _db.Jobs
            .OrderByDescending(j => j.CreatedAt)
            .Take(count)
            .Select(j => new RecentJob
            {
                Id = j.Id,
                Status = j.Status,
                RepoUrl = j.RepoUrl,
                Ref = j.Ref,
                CreatedAt = j.CreatedAt,
                WorkerId = j.WorkerId,
                WorkerName = j.WorkerId != null
                    ? _db.Workers.Where(w => w.Id == j.WorkerId).Select(w => w.Name).FirstOrDefault()
                    : null
            })
            .ToListAsync();

        return jobs;
    }
}

public record DashboardStats
{
    public int TotalJobs { get; init; }
    public int RunningJobs { get; init; }
    public int QueuedJobs { get; init; }
    public int SuccessfulJobs { get; init; }
    public int FailedJobs { get; init; }
    public int TotalWorkers { get; init; }
    public int ConnectedWorkers { get; init; }
}

public record RecentJob
{
    public required Ulid Id { get; init; }
    public required JobStatus Status { get; init; }
    public required string RepoUrl { get; init; }
    public required string Ref { get; init; }
    public required DateTime CreatedAt { get; init; }
    public Ulid? WorkerId { get; init; }
    public string? WorkerName { get; init; }
}
