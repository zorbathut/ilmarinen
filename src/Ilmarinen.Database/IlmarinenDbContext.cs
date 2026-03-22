using Ilmarinen.Database.Entities;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.EntityFrameworkCore;
using NUlid;
using System;

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
    public DbSet<Pipeline> Pipelines => Set<Pipeline>();
    public DbSet<Subscriber> Subscribers => Set<Subscriber>();
    public DbSet<Notification> Notifications => Set<Notification>();

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
            e.HasIndex(j => j.PipelineId);
        });

        modelBuilder.Entity<Pipeline>(e =>
        {
            e.HasIndex(p => p.Name).IsUnique();
        });

        modelBuilder.Entity<JobLogChunk>(e =>
        {
            e.HasIndex(c => new { c.JobId, c.SequenceNumber });
            e.HasOne(c => c.Job)
                .WithMany()
                .HasForeignKey(c => c.JobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Worker>(e =>
        {
            e.HasIndex(w => w.Name).IsUnique();
        });

        modelBuilder.Entity<JobArtifact>(e =>
        {
            e.HasIndex(a => a.JobId);
            e.HasOne(a => a.Job)
                .WithMany()
                .HasForeignKey(a => a.JobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Subscriber>(e =>
        {
            e.HasIndex(s => s.Name).IsUnique();
        });

        modelBuilder.Entity<Notification>(e =>
        {
            e.HasIndex(n => new { n.SubscriberId, n.IsProcessed });
            e.HasOne(n => n.Subscriber)
                .WithMany()
                .HasForeignKey(n => n.SubscriberId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(n => n.Job)
                .WithMany()
                .HasForeignKey(n => n.JobId)
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
