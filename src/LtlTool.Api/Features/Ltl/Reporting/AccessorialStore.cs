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
        var existing = db.AccessorialRecords.FirstOrDefault(a =>
            a.LoadId == loadId
            && a.TripId == tripId
            && a.EntityType == line.EntityType
            && a.Type == line.Type
            && a.Description == line.Description
            && a.Amount == line.Amount);

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

        return query
            .OrderByDescending(a => a.LastSeenAt)
            .Take(Math.Max(1, max))
            .ToList();
    }
}
