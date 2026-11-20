using Ilmarinen.Database;
using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Npgsql;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
[Category("Integration")]
public class DatabaseSchemaTests
{
    private TestPostgresContainer _postgres = null!;
    private IlmarinenDbContext _dbContext = null!;

    [SetUp]
    public async Task SetUp()
    {
        _postgres = new TestPostgresContainer(database: "ilmarinen_schema_test");
        await _postgres.StartAsync();

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(_postgres.ConnectionString);
        dataSourceBuilder.EnableDynamicJson();
        var dataSource = dataSourceBuilder.Build();

        var options = new DbContextOptionsBuilder<IlmarinenDbContext>()
            .UseNpgsql(dataSource)
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;

        _dbContext = new IlmarinenDbContext(options);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _dbContext.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Test]
    public void Model_HasNoPendingChanges()
    {
        // Equivalent to `dotnet ef migrations has-pending-model-changes`: compares the entity model (C# code) to the migration snapshot
        var migrationsAssembly = _dbContext.GetService<IMigrationsAssembly>();
        var snapshotModel = migrationsAssembly.ModelSnapshot?.Model;

        Assert.That(snapshotModel, Is.Not.Null, "Migration snapshot should exist");

        // Finalize the snapshot model if needed
        if (snapshotModel is IMutableModel mutableModel)
        {
            snapshotModel = mutableModel.FinalizeModel();
        }

        // Initialize runtime dependencies for the snapshot model
        var modelRuntimeInitializer = _dbContext.GetService<IModelRuntimeInitializer>();
        snapshotModel = modelRuntimeInitializer.Initialize(snapshotModel!);

        // Get design-time model which has full configuration
        var designTimeModel = _dbContext.GetService<IDesignTimeModel>();
        var modelDiffer = _dbContext.GetService<IMigrationsModelDiffer>();

        // Compare snapshot model to the design-time model
        var differences = modelDiffer.GetDifferences(
            snapshotModel.GetRelationalModel(),
            designTimeModel.Model.GetRelationalModel());

        Assert.That(differences, Is.Empty,
            "Entity model has changes not captured in migrations. " +
            "Run 'dotnet ef migrations add <Name>' to fix.");
    }

    [Test]
    public async Task AllMigrations_ApplySuccessfully()
    {
        // Equivalent to: dotnet ef database update
        await _dbContext.Database.MigrateAsync();

        var pendingMigrations = await _dbContext.Database.GetPendingMigrationsAsync();
        Assert.That(pendingMigrations, Is.Empty,
            "All migrations should apply without error.");
    }

    [Test]
    public async Task AddWorkerPriority_BackfillsExistingWorkersToMedium()
    {
        // Every other migration test runs against an empty database, so nothing exercises a backfill. This one does: EF's scaffolder defaults a new non-nullable enum column to 0, which here means Low — silently demoting an entire existing fleet.
        var migrator = _dbContext.GetService<IMigrator>();
        await migrator.MigrateAsync("AddWorkerLastIpAddress");

        var workerId = Guid.NewGuid();
        await InsertWorkerAsync(workerId, "legacy-worker", priority: null);

        await migrator.MigrateAsync("AddWorkerPriority");

        var priority = await _dbContext.Database.SqlQuery<int>($"""SELECT "Priority" AS "Value" FROM "Workers" WHERE "Id" = {workerId}""").SingleAsync();
        Assert.That(priority, Is.EqualTo(1), "a worker that predates the Priority column must land on Medium (1), not Low (0)");
    }

    // The old fixed values: worker Low 0, Medium 1, High 2; job Low -1, Medium 0, High 1.
    private const string LastFixedPrioritiesMigration = "AddWorkerWakeMac";

    private async Task InsertWorkerAsync(Guid id, string name, int? priority)
    {
        if (priority is { } value)
        {
            await _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Workers" ("Id", "Name", "PublicKey", "RegisteredAt", "LastSeen", "Priority")
                VALUES ({id}, {name}, '\x00'::bytea, now(), now(), {value})
                """);
            return;
        }
        await _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Workers" ("Id", "Name", "PublicKey", "RegisteredAt", "LastSeen")
            VALUES ({id}, {name}, '\x00'::bytea, now(), now())
            """);
    }

    private async Task InsertJobAsync(Guid id, int minWorkerPriority, int priority)
    {
        await _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Jobs" ("Id", "Status", "RepoUrl", "Ref", "ScriptPath", "CreatedAt", "GitTokenMode", "MinWorkerPriority", "Priority")
            VALUES ({id}, 4, 'https://example.invalid/repo.git', 'main', 'pipeline.csx', now(), 0, {minWorkerPriority}, {priority})
            """);
    }

    private async Task InsertPipelineAsync(Guid id, int minWorkerPriority, int priority)
    {
        var repositoryId = Guid.NewGuid();
        await _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Repositories" ("Id", "Name", "RepoUrl", "CreatedAt")
            VALUES ({repositoryId}, {"repo-" + id}, 'https://example.invalid/repo.git', now())
            """);
        await _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Pipelines" ("Id", "Name", "RepositoryId", "DefaultRef", "ScriptPath", "CreatedAt", "MinWorkerPriority", "Priority", "NotificationPolicy")
            VALUES ({id}, {"pipeline-" + id}, {repositoryId}, 'main', 'pipeline.csx', now(), {minWorkerPriority}, {priority}, 0)
            """);
    }

    private async Task<string[]> NamesAsync(PriorityKind kind)
    {
        return await _dbContext.Priorities.Where(p => p.Kind == kind).OrderByDescending(p => p.Rank).Select(p => p.Name).ToArrayAsync();
    }

    private async Task<string> DefaultAsync(PriorityKind kind)
    {
        return await _dbContext.Priorities.Where(p => p.Kind == kind && p.IsDefault).Select(p => p.Name).SingleAsync();
    }

    [Test]
    public async Task MakePrioritiesUserDefined_CreatesAnEntryForEachValueInUse_InTheOldOrder()
    {
        var migrator = _dbContext.GetService<IMigrator>();
        await migrator.MigrateAsync(LastFixedPrioritiesMigration);

        var mediumWorker = Guid.NewGuid();
        var highWorker = Guid.NewGuid();
        var anyWorkerLowJob = Guid.NewGuid();
        var mediumMinimumMediumJob = Guid.NewGuid();
        var pipeline = Guid.NewGuid();
        await InsertWorkerAsync(mediumWorker, "medium-worker", 1);
        await InsertWorkerAsync(highWorker, "high-worker", 2);
        await InsertJobAsync(anyWorkerLowJob, minWorkerPriority: 0, priority: -1);
        await InsertJobAsync(mediumMinimumMediumJob, minWorkerPriority: 1, priority: 0);
        await InsertPipelineAsync(pipeline, minWorkerPriority: 0, priority: 0);

        await migrator.MigrateAsync();

        Assert.That(await NamesAsync(PriorityKind.Worker), Is.EqualTo(new[] { "High", "Medium" }), "a Low minimum means any worker, so it uses no worker priority");
        Assert.That(await NamesAsync(PriorityKind.Job), Is.EqualTo(new[] { "Medium", "Low" }), "nothing used High");
        Assert.That(await DefaultAsync(PriorityKind.Worker), Is.EqualTo("Medium"));
        Assert.That(await DefaultAsync(PriorityKind.Job), Is.EqualTo("Medium"));

        var workers = await _dbContext.Workers.AsNoTracking().Include(w => w.Priority).ToDictionaryAsync(w => w.Id, w => w.Priority!.Name);
        Assert.That(workers[mediumWorker], Is.EqualTo("Medium"));
        Assert.That(workers[highWorker], Is.EqualTo("High"));

        var jobs = await _dbContext.Jobs.AsNoTracking().Include(j => j.Priority).Include(j => j.MinWorkerPriority).ToDictionaryAsync(j => j.Id);
        Assert.That(jobs[anyWorkerLowJob].MinWorkerPriority, Is.Null);
        Assert.That(jobs[anyWorkerLowJob].Priority!.Name, Is.EqualTo("Low"));
        Assert.That(jobs[mediumMinimumMediumJob].MinWorkerPriority!.Name, Is.EqualTo("Medium"));
        Assert.That(jobs[mediumMinimumMediumJob].Priority!.Name, Is.EqualTo("Medium"));

        var converted = await _dbContext.Pipelines.AsNoTracking().Include(p => p.Priority).SingleAsync(p => p.Id == pipeline);
        Assert.That(converted.MinWorkerPriorityId, Is.Null);
        Assert.That(converted.Priority!.Name, Is.EqualTo("Medium"));
    }

    [Test]
    public async Task MakePrioritiesUserDefined_OnAnEmptyDatabase_GivesEachKindJustDefault()
    {
        await _dbContext.Database.MigrateAsync();

        Assert.That(await NamesAsync(PriorityKind.Worker), Is.EqualTo(new[] { "Default" }));
        Assert.That(await NamesAsync(PriorityKind.Job), Is.EqualTo(new[] { "Default" }));
        Assert.That(await DefaultAsync(PriorityKind.Worker), Is.EqualTo("Default"));
        Assert.That(await DefaultAsync(PriorityKind.Job), Is.EqualTo("Default"));
    }

    [Test]
    public async Task MakePrioritiesUserDefined_WithoutMedium_MakesTheLowestEntryTheDefault()
    {
        var migrator = _dbContext.GetService<IMigrator>();
        await migrator.MigrateAsync(LastFixedPrioritiesMigration);
        await InsertWorkerAsync(Guid.NewGuid(), "high-worker", 2);
        await InsertJobAsync(Guid.NewGuid(), minWorkerPriority: 2, priority: 1);
        await InsertJobAsync(Guid.NewGuid(), minWorkerPriority: 0, priority: -1);

        await migrator.MigrateAsync();

        Assert.That(await NamesAsync(PriorityKind.Worker), Is.EqualTo(new[] { "High" }));
        Assert.That(await DefaultAsync(PriorityKind.Worker), Is.EqualTo("High"));
        Assert.That(await NamesAsync(PriorityKind.Job), Is.EqualTo(new[] { "High", "Low" }));
        Assert.That(await DefaultAsync(PriorityKind.Job), Is.EqualTo("Low"));
    }

    [Test]
    public async Task MakePrioritiesUserDefined_RefusesAValueOutsideTheOldOnes()
    {
        var migrator = _dbContext.GetService<IMigrator>();
        await migrator.MigrateAsync(LastFixedPrioritiesMigration);
        await InsertWorkerAsync(Guid.NewGuid(), "strange-worker", 7);

        var failure = Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync());

        Assert.That(failure!.MessageText, Does.Contain("MakePrioritiesUserDefined"));
    }
}
