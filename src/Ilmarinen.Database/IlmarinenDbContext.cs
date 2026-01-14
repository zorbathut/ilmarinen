using Ilmarinen.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NUlid;

namespace Ilmarinen.Database;

public class IlmarinenDbContext : DbContext
{
    public IlmarinenDbContext(DbContextOptions<IlmarinenDbContext> options)
        : base(options)
    {
    }

    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Worker> Workers => Set<Worker>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder
            .Properties<Ulid>()
            .HaveConversion<UlidToGuidConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Job>(e =>
        {
            e.HasIndex(j => j.Status);
            e.HasIndex(j => j.CreatedAt);
        });
    }
}

public class UlidToGuidConverter : ValueConverter<Ulid, Guid>
{
    public UlidToGuidConverter()
        : base(
            ulid => ulid.ToGuid(),
            guid => new Ulid(guid))
    {
    }
}
