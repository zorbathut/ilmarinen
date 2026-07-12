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
        await _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Workers" ("Id", "Name", "PublicKey", "RegisteredAt", "LastSeen")
            VALUES ({workerId}, 'legacy-worker', '\x00'::bytea, now(), now())
            """);

        await migrator.MigrateAsync();

        var worker = await _dbContext.Workers.AsNoTracking().SingleAsync(w => w.Id == workerId);
        Assert.That(worker.Priority, Is.EqualTo(WorkerPriority.Medium),
            "a worker that predates the Priority column must land on Medium, not Low");
    }
}
