using LtlTool.Api.Features.Integrations.Alvys.Webhooks;
using LtlTool.Api.Features.Integrations.Alvys.Writeback;
using LtlTool.Api.Features.Ltl.Assignment;
using LtlTool.Api.Features.Ltl.Consolidation;
using LtlTool.Api.Features.Ltl.SavedViews;
using LtlTool.Api.Features.Ltl.Signals;
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

    /// <summary>Durable internal assignment-decision audit trail (see <see cref="EfAssignmentAuditStore"/>). Never written back to Alvys.</summary>
    public DbSet<AssignmentAuditRecord> AssignmentAudits => Set<AssignmentAuditRecord>();

    /// <summary>Durable recurring-lane templates (see <see cref="EfLaneTemplateStore"/>). Internal lane-shape notes, never Alvys.</summary>
    public DbSet<LaneTemplateRecord> LaneTemplates => Set<LaneTemplateRecord>();

    /// <summary>Durable Phase 6 extracted LTL signals (see <see cref="EfSignalStore"/>). Internal data, never Alvys.</summary>
    public DbSet<SignalRecord> Signals => Set<SignalRecord>();

    /// <summary>Durable received Alvys webhook deliveries (see <see cref="EfAlvysWebhookStore"/>). Idempotency-keyed by event id.</summary>
    public DbSet<AlvysWebhookEvent> AlvysWebhookEvents => Set<AlvysWebhookEvent>();

    /// <summary>Per-load freshness markers derived from webhook deliveries (see <see cref="EfAlvysWebhookStore"/>). A pointer only — Alvys stays authoritative.</summary>
    public DbSet<LoadFreshnessRecord> LoadFreshness => Set<LoadFreshnessRecord>();

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
            // Post-write reconciliation (document uploads). Enum stored as a readable string.
            entity.Property(e => e.ReconciliationState).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.ReconciliationDetail).HasMaxLength(2048);
            entity.Property(e => e.ResultReference).HasMaxLength(1024);

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

        modelBuilder.Entity<AssignmentAuditRecord>(entity =>
        {
            entity.ToTable("AssignmentAudits");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64);
            entity.Property(e => e.LoadId).IsRequired().HasMaxLength(128);
            entity.Property(e => e.DriverId).HasMaxLength(128);
            entity.Property(e => e.TruckId).HasMaxLength(128);
            entity.Property(e => e.TrailerId).HasMaxLength(128);
            entity.Property(e => e.MatchLabel).HasMaxLength(64);
            entity.Property(e => e.Notes).HasMaxLength(4000);
            // Enum stored as a readable string so the table is legible; legacy/unspecified reads
            // back as "Unspecified" with the free-text detail preserved.
            entity.Property(e => e.ReasonType).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.OverrideReason).HasMaxLength(1024);
            entity.Property(e => e.WarningsJson).IsRequired();
            entity.Property(e => e.RecordedBy).IsRequired().HasMaxLength(256);
            entity.Property(e => e.AlvysWriteback).IsRequired().HasMaxLength(32);

            // History listing is keyed by load (per-load drawer) and filtered by user (history page).
            entity.HasIndex(e => e.LoadId);
            entity.HasIndex(e => e.RecordedBy);
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

        modelBuilder.Entity<SignalRecord>(entity =>
        {
            entity.ToTable("Signals");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64);
            entity.Property(e => e.SourceType).IsRequired().HasMaxLength(32);
            entity.Property(e => e.SourceId).IsRequired().HasMaxLength(256);
            // Enums stored as readable strings so the audit table is legible in the database.
            entity.Property(e => e.SignalType).IsRequired().HasMaxLength(48);
            entity.Property(e => e.SuggestedSurface).IsRequired().HasMaxLength(32);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(16);
            // Evidence quote is mandatory (fail-closed); nvarchar(max) so a long snippet round-trips.
            entity.Property(e => e.EvidenceQuote).IsRequired();
            entity.Property(e => e.Summary).HasMaxLength(512);
            entity.Property(e => e.LoadNumber).HasMaxLength(64);
            entity.Property(e => e.IngestedBy).IsRequired().HasMaxLength(256);
            entity.Property(e => e.DecidedBy).HasMaxLength(256);

            // Review-queue listing (by status, newest first) and per-load surfacing.
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.LoadNumber);
        });

        modelBuilder.Entity<AlvysWebhookEvent>(entity =>
        {
            entity.ToTable("AlvysWebhookEvents");
            // The Alvys-supplied event id is the natural idempotency key: a duplicate delivery of the
            // same id is rejected on insert (the PK gives us the unique index for free).
            entity.HasKey(e => e.EventId);
            entity.Property(e => e.EventId).HasMaxLength(128);
            entity.Property(e => e.EventType).IsRequired().HasMaxLength(128);
            entity.Property(e => e.LoadNumber).HasMaxLength(64);
            // Raw body stored verbatim for audit/replay; nvarchar(max) so a large snapshot round-trips.
            entity.Property(e => e.RawBody).IsRequired();
            // Enum stored as a readable string so the table is legible in the database.
            entity.Property(e => e.ProcessingState).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.ProcessingError).HasMaxLength(2048);

            // Admin listing is newest-first; per-load freshness derivation filters by load number.
            entity.HasIndex(e => e.ReceivedAt);
            entity.HasIndex(e => e.LoadNumber);
        });

        modelBuilder.Entity<LoadFreshnessRecord>(entity =>
        {
            entity.ToTable("LoadFreshness");
            entity.HasKey(e => e.LoadNumber);
            entity.Property(e => e.LoadNumber).HasMaxLength(64);
            entity.Property(e => e.LastEventType).HasMaxLength(128);
            entity.Property(e => e.LastEventId).HasMaxLength(128);
        });
    }
}
