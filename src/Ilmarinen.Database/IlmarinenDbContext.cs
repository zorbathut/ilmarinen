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
    public DbSet<JobLogChunk> JobLogChunks => Set<JobLogChunk>();
    public DbSet<JobArtifact> JobArtifacts => Set<JobArtifact>();

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

        modelBuilder.Entity<JobLogChunk>(e =>
        {
            e.HasIndex(c => new { c.JobId, c.SequenceNumber });
            e.HasOne(c => c.Job)
                .WithMany()
                .HasForeignKey(c => c.JobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<JobArtifact>(e =>
        {
            e.HasIndex(a => a.JobId);
            e.HasOne(a => a.Job)
                .WithMany()
                .HasForeignKey(a => a.JobId)
                .OnDelete(DeleteBehavior.Cascade);
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
