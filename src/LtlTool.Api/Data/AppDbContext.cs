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
    }
}
