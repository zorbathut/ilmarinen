using Ilmarinen.Database.Entities;
using Microsoft.EntityFrameworkCore;
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
    public DbSet<JobMetricsBatch> JobMetricsBatches => Set<JobMetricsBatch>();
    public DbSet<JobStep> JobSteps => Set<JobStep>();
    public DbSet<Repository> Repositories => Set<Repository>();
    public DbSet<Subscriber> Subscribers => Set<Subscriber>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Priority> Priorities => Set<Priority>();

    /// <summary>The collation priority names are compared in: case-insensitive, so "High" and "high" are one name.</summary>
    public const string CaseInsensitiveCollation = "case_insensitive";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasCollation(CaseInsensitiveCollation, locale: "und-u-ks-level2", provider: "icu", deterministic: false);

        modelBuilder.Entity<Priority>(e =>
        {
            e.Property(p => p.Name).UseCollation(CaseInsensitiveCollation);
            e.HasIndex(p => new { p.Kind, p.Name }).IsUnique();
            e.HasIndex(p => p.Kind).IsUnique().HasFilter("\"IsDefault\"").HasDatabaseName("IX_Priorities_Kind_Default");
        });

        modelBuilder.Entity<Job>(e =>
        {
            e.HasOne(j => j.Priority).WithMany().HasForeignKey(j => j.PriorityId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(j => j.MinWorkerPriority).WithMany().HasForeignKey(j => j.MinWorkerPriorityId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(j => j.Status);
            e.HasIndex(j => j.CreatedAt);
            e.HasIndex(j => j.PipelineId);
            e.HasIndex(j => new { j.WorkerId, j.Status });
            // Duration estimates read the latest successes of a script, often of every queued one at once.
            e.HasIndex(j => new { j.RepoUrl, j.ScriptPath, j.Status, j.CompletedAt });
        });

        modelBuilder.Entity<Pipeline>(e =>
        {
            e.HasIndex(p => p.Name).IsUnique();
            e.HasOne(p => p.Repository)
                .WithMany()
                .HasForeignKey(p => p.RepositoryId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(p => p.Priority).WithMany().HasForeignKey(p => p.PriorityId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(p => p.MinWorkerPriority).WithMany().HasForeignKey(p => p.MinWorkerPriorityId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Repository>(e =>
        {
            e.HasIndex(r => r.Name).IsUnique();
        });

        modelBuilder.Entity<JobMetricsBatch>(e =>
        {
            e.HasIndex(b => new { b.JobId, b.SequenceNumber });
            e.HasOne(b => b.Job)
                .WithMany()
                .HasForeignKey(b => b.JobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<JobStep>(e =>
        {
            e.HasKey(s => new { s.JobId, s.Index });
            e.HasOne(s => s.Job)
                .WithMany()
                .HasForeignKey(s => s.JobId)
                .OnDelete(DeleteBehavior.Cascade);
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
            e.HasOne(w => w.Priority).WithMany().HasForeignKey(w => w.PriorityId).OnDelete(DeleteBehavior.Restrict);
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
