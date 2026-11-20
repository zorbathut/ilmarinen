using Ilmarinen.Database.Entities;
using Ilmarinen.Database;
using Ilmarinen.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Fixtures;

/// <summary>
/// The priorities most tests run with: Low, Medium and High in both kinds, Medium the default, as a server migrated from the old fixed priorities has them when all three were in use.
/// </summary>
public static class StandardPriority
{
    public const string Low = "Low";
    public const string Medium = "Medium";
    public const string High = "High";

    /// <summary>Turns a fresh install's lone "Default" in each kind into Medium, with Low below it and High above.</summary>
    public static async Task SetUpAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        foreach (var kind in new[] { PriorityKind.Worker, PriorityKind.Job })
        {
            var only = await db.Priorities.SingleAsync(p => p.Kind == kind);
            only.Name = Medium;
            only.Rank = 1;
            db.Priorities.Add(new Priority { Id = Guid.NewGuid(), Kind = kind, Name = Low, Rank = 0 });
            db.Priorities.Add(new Priority { Id = Guid.NewGuid(), Kind = kind, Name = High, Rank = 2 });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>The default job priority's id, for a test that writes jobs to the database itself.</summary>
    public static async Task<Guid> DefaultJobPriorityIdAsync(IlmarinenDbContext db)
    {
        return await db.Priorities.Where(p => p.Kind == PriorityKind.Job && p.IsDefault).Select(p => p.Id).SingleAsync();
    }

    public static async Task<Guid> IdAsync(IServiceProvider services, PriorityKind kind, string name)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        return await db.Priorities.Where(p => p.Kind == kind && p.Name == name).Select(p => p.Id).SingleAsync();
    }
}

/// <summary>The ranks <see cref="StandardPriority"/> gives its worker priorities, for tests of the pure planners that take ranks.</summary>
public static class StandardRank
{
    public const int Low = 0;
    public const int Medium = 1;
    public const int High = 2;
}
