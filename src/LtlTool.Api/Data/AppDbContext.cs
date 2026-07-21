using LtlTool.Api.Features.Integrations.Alvys.Writeback;
using LtlTool.Api.Features.Ltl.Consolidation;
using LtlTool.Api.Features.Ltl.SavedViews;
using LtlTool.Api.Features.Ltl.YardArtifacts;
using Microsoft.EntityFrameworkCore;

namespace LtlTool.Api.Data;

/// <summary>
/// Application database context. Add your DbSet&lt;T&gt; entities here as your
/// application grows.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    /// <summary>Durable, owner-scoped dispatcher saved views (see <see cref="EfSavedViewStore"/>).</summary>
    public DbSet<SavedViewRecord> SavedViews => Set<SavedViewRecord>();

    /// <summary>Durable, owner-scoped Alvys operation outbox/audit records (see <see cref="EfAlvysOperationStore"/>).</summary>
    public DbSet<AlvysOperationRecord> AlvysOperations => Set<AlvysOperationRecord>();

    /// <summary>Durable yard-artifact intake metadata (see <see cref="EfYardArtifactStore"/>). Internal data, never Alvys.</summary>
    public DbSet<YardArtifactRecord> YardArtifacts => Set<YardArtifactRecord>();

    /// <summary>Durable recurring-lane templates (see <see cref="EfLaneTemplateStore"/>). Internal lane-shape notes, never Alvys.</summary>
    public DbSet<LaneTemplateRecord> LaneTemplates => Set<LaneTemplateRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<SavedViewRecord>(entity =>
        {
            entity.ToTable("SavedViews");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64);
            entity.Property(e => e.OwnerId).IsRequired().HasMaxLength(256);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(80);
            entity.Property(e => e.Description).HasMaxLength(280);
            entity.Property(e => e.FiltersJson).IsRequired();
            // Owner scope is the only relational predicate; index it so per-dispatcher lists stay cheap.
            entity.HasIndex(e => e.OwnerId);
        });

        modelBuilder.Entity<AlvysOperationRecord>(entity =>
        {
            entity.ToTable("AlvysOperations");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64);
            entity.Property(e => e.OwnerId).IsRequired().HasMaxLength(256);
            entity.Property(e => e.OperationCode).IsRequired().HasMaxLength(64);
            // Enums are stored as readable strings so the audit table is legible in the database.
            entity.Property(e => e.Channel).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.ResourceType).HasMaxLength(32);
            entity.Property(e => e.ResourceId).HasMaxLength(128);
            entity.Property(e => e.IdempotencyKey).HasMaxLength(128);
            entity.Property(e => e.PayloadHash).IsRequired().HasMaxLength(64);
            entity.Property(e => e.PayloadPreview).HasMaxLength(4000);
            entity.Property(e => e.Mode).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.Disposition).HasConversion<string>().HasMaxLength(24);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(24);
            entity.Property(e => e.Reason).HasMaxLength(1024);
            entity.Property(e => e.LastError).HasMaxLength(2048);
            entity.Property(e => e.CorrelationId).IsRequired().HasMaxLength(64);

            // Owner-scoped history listing, newest first.
            entity.HasIndex(e => new { e.OwnerId, e.CreatedAt });
            // Idempotency backstop: an owner's executable idempotency key is unique. Filtered so the
            // many records without a key (dry-runs, keyless executes) never collide on NULL.
            entity.HasIndex(e => new { e.OwnerId, e.IdempotencyKey })
                .IsUnique()
                .HasFilter("[IdempotencyKey] IS NOT NULL AND [Channel] = 'Execute'");
        });

        modelBuilder.Entity<YardArtifactRecord>(entity =>
        {
            entity.ToTable("YardArtifacts");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64);
            entity.Property(e => e.Yard).IsRequired().HasMaxLength(16);
            entity.Property(e => e.TruckUnit).HasMaxLength(64);
            entity.Property(e => e.TrailerUnit).HasMaxLength(64);
            entity.Property(e => e.LoadNumber).HasMaxLength(64);
            entity.Property(e => e.SubmittedBy).IsRequired().HasMaxLength(256);
            // Enum stored as a readable string so the table is legible in the database.
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.InspectionJson).IsRequired();
            entity.Property(e => e.FilesJson).IsRequired();

            // Surfacing lookups: arrivals board joins by equipment unit, load detail by load number.
            entity.HasIndex(e => e.LoadNumber);
            entity.HasIndex(e => e.TruckUnit);
            entity.HasIndex(e => e.TrailerUnit);
        });

        modelBuilder.Entity<LaneTemplateRecord>(entity =>
        {
            entity.ToTable("LaneTemplates");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(120);
            entity.Property(e => e.CorridorCode).IsRequired().HasMaxLength(64);
            entity.Property(e => e.CustomerName).HasMaxLength(256);
            entity.Property(e => e.OriginLabel).HasMaxLength(256);
            entity.Property(e => e.DestinationLabel).HasMaxLength(256);
            entity.Property(e => e.Notes).HasMaxLength(1024);
            entity.Property(e => e.CreatedBy).IsRequired().HasMaxLength(256);

            // Templates are listed by corridor and/or customer; index both relational predicates.
            entity.HasIndex(e => e.CorridorCode);
            entity.HasIndex(e => e.CustomerName);
        });
    }
}
