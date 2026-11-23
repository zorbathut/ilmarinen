using Ilmarinen.Database;
using Ilmarinen.Protocol;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Server.Services;

public class DashboardService
{
    private readonly IlmarinenDbContext _db;
    private readonly WorkerRepository _workers;

    public DashboardService(IlmarinenDbContext db, WorkerRepository workers)
    {
        _db = db;
        _workers = workers;
    }

    public async Task<DashboardStats> GetStatsAsync()
    {
        var jobs = await _db.Jobs.ToListAsync();
        var totalWorkers = await _db.Workers.CountAsync();
        var pipelineCount = await _db.Pipelines.CountAsync();

        // Derive connected count from in-memory state
        var allWorkers = await _workers.GetAllAsync();
        var connectedWorkers = allWorkers.Count(w => w.IsConnected);

        return new DashboardStats
        {
            TotalJobs = jobs.Count,
            RunningJobs = jobs.Count(j => j.Status == JobStatus.Running),
            QueuedJobs = jobs.Count(j => j.Status == JobStatus.Queued),
            SuccessfulJobs = jobs.Count(j => j.Status == JobStatus.Success),
            FailedJobs = jobs.Count(j => j.Status == JobStatus.Failed),
            TotalWorkers = totalWorkers,
            ConnectedWorkers = connectedWorkers,
            TotalPipelines = pipelineCount
        };
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
    public int TotalPipelines { get; init; }
}
