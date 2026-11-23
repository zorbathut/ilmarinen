using Ilmarinen.Database.Entities;
using Ilmarinen.Database;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Server.Services;

/// <summary>
/// The operator's lists of worker and job priorities, and turning the names requests use into entries.
/// </summary>
public class PriorityRepository
{
    public const int MaxNameLength = 64;

    // The foreign keys that refer to a priority, as the migration names them.
    private static readonly HashSet<string> ReferenceConstraints =
    [
        "FK_Workers_Priorities_PriorityId",
        "FK_Pipelines_Priorities_PriorityId",
        "FK_Pipelines_Priorities_MinWorkerPriorityId",
        "FK_Jobs_Priorities_PriorityId",
        "FK_Jobs_Priorities_MinWorkerPriorityId"
    ];

    private readonly IlmarinenDbContext _db;

    public PriorityRepository(IlmarinenDbContext db)
    {
        _db = db;
    }

    /// <summary>A kind's entries, highest first. Read afresh each time, not from what the context tracks: a page may hold its context across changes made elsewhere.</summary>
    public async Task<IReadOnlyList<Priority>> GetAllAsync(PriorityKind kind)
    {
        return await _db.Priorities
            .AsNoTracking()
            .Where(p => p.Kind == kind)
            .OrderByDescending(p => p.Rank)
            .ThenBy(p => p.Id)
            .ToListAsync();
    }

    /// <summary>Every priority's rank, of both kinds, by id: what a list sorts priorities by.</summary>
    public async Task<IReadOnlyDictionary<Guid, int>> GetRanksAsync()
    {
        return await _db.Priorities.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Rank);
    }

    public async Task<Priority> GetDefaultAsync(PriorityKind kind)
    {
        return await _db.Priorities.AsNoTracking().SingleAsync(p => p.Kind == kind && p.IsDefault);
    }

    /// <summary>The entry of this kind with this name, in any case; an unknown name is the caller's mistake.</summary>
    public async Task<Priority> ResolveAsync(PriorityKind kind, string name)
    {
        var trimmed = name.Trim();

        // The column's collation makes this comparison case-insensitive.
        var priority = await _db.Priorities.AsNoTracking().FirstOrDefaultAsync(p => p.Kind == kind && p.Name == trimmed);
        if (priority != null)
        {
            return priority;
        }

        throw new InvalidSubmissionException($"There is no {Describe(kind)} called \"{trimmed}\". {await KnownNamesAsync(kind)}.");
    }

    private async Task<string> KnownNamesAsync(PriorityKind kind)
    {
        return $"The {Describe(kind)} names are: {string.Join(", ", (await GetAllAsync(kind)).Select(p => p.Name))}";
    }

    /// <summary>A minimum worker priority as a request names it: null for <see cref="PriorityNames.AnyWorker"/>, which asks for no minimum.</summary>
    public async Task<Priority?> ResolveMinimumAsync(string name)
    {
        if (IsAnyWorker(name))
        {
            return null;
        }
        var trimmed = name.Trim();
        var priority = await _db.Priorities.AsNoTracking().FirstOrDefaultAsync(p => p.Kind == PriorityKind.Worker && p.Name == trimmed);
        return priority ?? throw new InvalidSubmissionException($"There is no {Describe(PriorityKind.Worker)} called \"{trimmed}\". {await KnownNamesAsync(PriorityKind.Worker)}, or \"{PriorityNames.AnyWorker}\" for no minimum.");
    }

    /// <summary>What uses each priority, by id; a priority nothing uses has no entry.</summary>
    public async Task<IReadOnlyDictionary<Guid, PriorityUsage>> GetUsageAsync()
    {
        static async Task<Dictionary<Guid, int>> CountAsync(IQueryable<Guid> ids)
        {
            return await ids.GroupBy(id => id).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count);
        }

        var workers = await CountAsync(_db.Workers.Select(w => w.PriorityId));
        var pipelines = await CountAsync(_db.Pipelines.Select(p => p.PriorityId));
        var jobs = await CountAsync(_db.Jobs.Select(j => j.PriorityId));
        var pipelinesNeeding = await CountAsync(_db.Pipelines.Where(p => p.MinWorkerPriorityId != null).Select(p => p.MinWorkerPriorityId!.Value));
        var jobsNeeding = await CountAsync(_db.Jobs.Where(j => j.MinWorkerPriorityId != null).Select(j => j.MinWorkerPriorityId!.Value));

        return workers.Keys.Concat(pipelines.Keys).Concat(jobs.Keys).Concat(pipelinesNeeding.Keys).Concat(jobsNeeding.Keys)
            .Distinct()
            .ToDictionary(id => id, id => new PriorityUsage
            {
                Workers = workers.GetValueOrDefault(id),
                Pipelines = pipelines.GetValueOrDefault(id),
                Jobs = jobs.GetValueOrDefault(id),
                PipelinesNeedingIt = pipelinesNeeding.GetValueOrDefault(id),
                JobsNeedingIt = jobsNeeding.GetValueOrDefault(id)
            });
    }

    public async Task<Priority?> FindAsync(Guid id)
    {
        return await _db.Priorities.FirstOrDefaultAsync(p => p.Id == id);
    }

    /// <summary>Adds an entry at the bottom of its list. Use through JobScheduler.ChangePrioritiesAsync, as every change to the lists.</summary>
    public async Task<Priority> CreateAsync(PriorityKind kind, string name)
    {
        var clean = await ValidNameAsync(kind, name, renaming: null);
        var lowest = await _db.Priorities.Where(p => p.Kind == kind).MinAsync(p => (int?)p.Rank) ?? 1;
        var priority = new Priority { Id = Guid.CreateVersion7(), Kind = kind, Name = clean, Rank = lowest - 1 };
        _db.Priorities.Add(priority);
        await _db.SaveChangesAsync();
        return priority;
    }

    /// <summary>Renames an entry; false if there is none with this id.</summary>
    public async Task<bool> RenameAsync(Guid id, string name)
    {
        var priority = await FindAsync(id);
        if (priority == null)
        {
            return false;
        }
        priority.Name = await ValidNameAsync(priority.Kind, name, renaming: id);
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>Makes an entry its kind's default, which the kind's previous default stops being; false if there is none with this id.</summary>
    public async Task<bool> SetDefaultAsync(Guid id)
    {
        var priority = await FindAsync(id);
        if (priority == null)
        {
            return false;
        }

        // The old default is cleared first: the database allows only one per kind at any moment.
        await using var transaction = await _db.Database.BeginTransactionAsync();
        await _db.Priorities.Where(p => p.Kind == priority.Kind && p.IsDefault).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsDefault, false));
        await _db.Priorities.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsDefault, true));
        await transaction.CommitAsync();
        return true;
    }

    /// <summary>Puts a kind's entries in this order, highest first. Every entry of the kind must be named exactly once.</summary>
    public async Task ReorderAsync(PriorityKind kind, IReadOnlyList<Guid> highestFirst)
    {
        var priorities = await _db.Priorities.Where(p => p.Kind == kind).ToListAsync();
        if (highestFirst.Count != priorities.Count || !highestFirst.ToHashSet().SetEquals(priorities.Select(p => p.Id)))
        {
            throw new InvalidSubmissionException($"A new order must list each {Describe(kind)} exactly once.");
        }

        var byId = priorities.ToDictionary(p => p.Id);
        for (var i = 0; i < highestFirst.Count; i++)
        {
            byId[highestFirst[i]].Rank = highestFirst.Count - 1 - i;
        }
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Deletes an entry, moving every worker, pipeline and job that uses it, finished jobs included, onto the replacement, as it does the default if it was the default. False if there is no entry with this id.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid id, Guid replacementId)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();

        // Locked first, so a write that refers to it waits for this one and then finds it gone, rather than landing between the moves below and the delete.
        var priority = (await _db.Priorities.FromSqlInterpolated($"""SELECT * FROM "Priorities" WHERE "Id" = {id} FOR UPDATE""").AsNoTracking().ToListAsync()).SingleOrDefault();
        if (priority == null)
        {
            return false;
        }

        var replacement = await FindAsync(replacementId);
        if (replacementId == id || replacement == null || replacement.Kind != priority.Kind)
        {
            var others = await _db.Priorities.CountAsync(p => p.Kind == priority.Kind && p.Id != id);
            throw new InvalidSubmissionException(others == 0
                ? $"\"{priority.Name}\" is the only {Describe(priority.Kind)}, so there is nothing to replace it with; add another first."
                : $"Deleting \"{priority.Name}\" needs another {Describe(priority.Kind)} to move what uses it onto.");
        }

        await _db.Workers.Where(w => w.PriorityId == id).ExecuteUpdateAsync(s => s.SetProperty(w => w.PriorityId, replacementId));
        await _db.Pipelines.Where(p => p.PriorityId == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.PriorityId, replacementId));
        await _db.Pipelines.Where(p => p.MinWorkerPriorityId == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.MinWorkerPriorityId, replacementId));
        await _db.Jobs.Where(j => j.PriorityId == id).ExecuteUpdateAsync(s => s.SetProperty(j => j.PriorityId, replacementId));
        await _db.Jobs.Where(j => j.MinWorkerPriorityId == id).ExecuteUpdateAsync(s => s.SetProperty(j => j.MinWorkerPriorityId, replacementId));
        await _db.Priorities.Where(p => p.Id == id).ExecuteDeleteAsync();
        if (priority.IsDefault)
        {
            await _db.Priorities.Where(p => p.Id == replacementId).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsDefault, true));
        }

        await transaction.CommitAsync();
        return true;
    }

    /// <summary>A name as it is stored: trimmed, not blank, not too long, not one the kind already has in any case (but its own, when renaming), and for a worker priority not the word a minimum uses for none.</summary>
    private async Task<string> ValidNameAsync(PriorityKind kind, string name, Guid? renaming)
    {
        var clean = name.Trim();
        if (clean.Length == 0)
        {
            throw new InvalidSubmissionException("A priority needs a name.");
        }
        if (clean.Length > MaxNameLength)
        {
            throw new InvalidSubmissionException($"A priority's name can be at most {MaxNameLength} characters long.");
        }
        if (kind == PriorityKind.Worker && IsAnyWorker(clean))
        {
            throw new InvalidSubmissionException($"\"{PriorityNames.AnyWorker}\" can't be a worker priority's name: it is how a job asks for no minimum worker priority.");
        }
        if (await _db.Priorities.AnyAsync(p => p.Kind == kind && p.Name == clean && p.Id != renaming))
        {
            throw new InvalidSubmissionException($"There is already a {Describe(kind)} called \"{clean}\".");
        }
        return clean;
    }

    public static bool IsAnyWorker(string name)
    {
        return string.Equals(name.Trim(), PriorityNames.AnyWorker, StringComparison.OrdinalIgnoreCase);
    }

    public static PriorityRef ToRef(Priority priority)
    {
        return new PriorityRef(priority.Id, priority.Name);
    }

    public static string Describe(PriorityKind kind)
    {
        return kind == PriorityKind.Worker ? "worker priority" : "job priority";
    }

    /// <summary>
    /// Runs a write that refers to priorities. A priority looked up for it can be deleted before the write lands; the foreign key then refuses the write, and that is reported as the caller's stale choice rather than a server error.
    /// </summary>
    public static async Task<T> WritingReferencesAsync<T>(Func<Task<T>> write)
    {
        try
        {
            return await write();
        }
        catch (Exception ex) when (IsDeletedPriorityReference(ex))
        {
            throw new InvalidSubmissionException("A priority this refers to was deleted meanwhile; choose another and try again.");
        }
    }

    private static bool IsDeletedPriorityReference(Exception ex)
    {
        var postgres = ex as PostgresException ?? ex.InnerException as PostgresException;
        return postgres is { SqlState: PostgresErrorCodes.ForeignKeyViolation, ConstraintName: { } constraint } && ReferenceConstraints.Contains(constraint);
    }
}
