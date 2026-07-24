using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LtlTool.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LtlTool.Api.Features.Ltl.Reporting;

/// <summary>
/// A single accessorial line observed on a load/trip read, passed to
/// <see cref="IAccessorialStore.Capture"/> for content-keyed upsert. See <see cref="AccessorialRecord"/>
/// for why there is no stable Alvys id to key on instead.
/// </summary>
public sealed record ObservedAccessorialLine(
    AccessorialEntityType EntityType,
    string? Type,
    string? Description,
    decimal? Amount);

/// <summary>
/// Durable, content-keyed store for normalized accessorial history (see <see cref="AccessorialRecord"/>).
/// Internal reporting data only — never read back into any live decision path; Alvys stays the
/// authoritative source for the live accessorial values shown elsewhere in the app.
/// </summary>
public interface IAccessorialStore
{
    /// <summary>
    /// Upserts one observed line: an exact content match (LoadId+TripId+EntityType+Type+
    /// Description+Amount) advances <see cref="AccessorialRecord.LastSeenAt"/>; anything else
    /// inserts a new row. Never throws on a store failure — callers treat capture as best-effort.
    /// </summary>
    void Capture(
        string loadId, string? loadNumber, string? tripId, ObservedAccessorialLine line, DateTimeOffset now);

    /// <summary>Recent accessorial lines (most-recently-seen first), optionally filtered by load and/or entity type.</summary>
    IReadOnlyList<AccessorialRecord> List(string? loadId, AccessorialEntityType? entityType, int max);
}

/// <inheritdoc cref="IAccessorialStore"/>
public sealed class EfAccessorialStore(AppDbContext db) : IAccessorialStore
{
    public void Capture(
        string loadId, string? loadNumber, string? tripId, ObservedAccessorialLine line, DateTimeOffset now)
    {
        var contentKey = ComputeContentKey(loadId, tripId, line);
        var existing = db.AccessorialRecords.FirstOrDefault(a => a.ContentKey == contentKey);

        if (existing is not null)
        {
            existing.LastSeenAt = now;
            // A load number learned after the fact (e.g. first captured before Alvys assigned one)
            // backfills onto the existing row rather than forcing a duplicate.
            if (string.IsNullOrWhiteSpace(existing.LoadNumber) && !string.IsNullOrWhiteSpace(loadNumber))
            {
                existing.LoadNumber = loadNumber;
            }
        }
        else
        {
            db.AccessorialRecords.Add(new AccessorialRecord
            {
                Id = Guid.NewGuid().ToString("n"),
                ContentKey = contentKey,
                LoadId = loadId,
                LoadNumber = loadNumber,
                TripId = tripId,
                EntityType = line.EntityType,
                Type = line.Type,
                Description = line.Description,
                Amount = line.Amount,
                FirstSeenAt = now,
                LastSeenAt = now,
            });
        }

        db.SaveChanges();
    }

    public IReadOnlyList<AccessorialRecord> List(string? loadId, AccessorialEntityType? entityType, int max)
    {
        IQueryable<AccessorialRecord> query = db.AccessorialRecords.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(loadId)) query = query.Where(a => a.LoadId == loadId);
        if (entityType is not null) query = query.Where(a => a.EntityType == entityType.Value);

        // AsEnumerable() before ordering: SQLite's EF provider cannot translate ORDER BY over a
        // DateTimeOffset column to SQL (see EfAssignmentAuditStore.ForLoad for the same constraint).
        // Pulls the filtered set fully into memory on every provider this runs against — accepted for
        // a moderate-volume internal reporting table (see ILoadAssignmentStore.ListRecent for the
        // same tradeoff spelled out).
        return query
            .AsEnumerable()
            .OrderByDescending(a => a.LastSeenAt)
            .Take(Math.Max(1, max))
            .ToList();
    }

    /// <summary>
    /// Deterministic content key for the dedupe match — see <see cref="AccessorialRecord.ContentKey"/>
    /// for why this exists instead of indexing the raw columns. Accessorial <c>Type</c>/<c>Description</c>
    /// are free-text from Alvys and could contain any character including a plain delimiter, so each
    /// field is length-prefixed before hashing — two different field splits must never hash
    /// identically just because a naive join of them would coincide.
    /// </summary>
    private static string ComputeContentKey(string loadId, string? tripId, ObservedAccessorialLine line)
    {
        var sb = new StringBuilder();
        AppendField(sb, loadId);
        AppendField(sb, tripId ?? "");
        AppendField(sb, line.EntityType.ToString());
        AppendField(sb, line.Type ?? "");
        AppendField(sb, line.Description ?? "");
        AppendField(sb, line.Amount?.ToString(CultureInfo.InvariantCulture) ?? "");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    private static void AppendField(StringBuilder sb, string value) =>
        sb.Append(value.Length).Append(':').Append(value);
}
