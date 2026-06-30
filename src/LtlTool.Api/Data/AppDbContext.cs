using LtlTool.Api.Features.Integrations.Alvys.Writeback;
using LtlTool.Api.Features.Ltl.SavedViews;
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
    }
}
