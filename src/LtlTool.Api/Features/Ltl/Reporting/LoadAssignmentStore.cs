using LtlTool.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LtlTool.Api.Features.Ltl.Reporting;

/// <summary>
/// A point-in-time assignment snapshot observed on a trip read, passed to
/// <see cref="ILoadAssignmentStore.CaptureIfChanged"/>. Carries only the fields that determine
/// whether "the assignment changed" — <see cref="LoadAssignmentRecord.Id"/>/<see cref="LoadAssignmentRecord.CapturedAt"/>
/// are assigned by the store itself.
/// </summary>
public sealed record ObservedAssignment(
    string LoadId,
    string? LoadNumber,
    string? TripId,
    string? Status,
    string? CarrierId,
    string? CarrierName,
    string? Driver1Id,
    string? Driver1Name,
    string? Driver2Id,
    string? Driver2Name,
    string? OwnerOperatorId,
    string? OwnerOperatorName,
    string? TruckId,
    string? TrailerId,
    string? DispatcherId,
    string? DispatchedBy,
    DateTimeOffset? CarrierAssignedAt);

/// <summary>
/// Durable, append-only (on change) store for assignment history (see <see cref="LoadAssignmentRecord"/>).
/// Internal reporting data only — never read back into any live decision path; Alvys stays the
/// authoritative source for the load's current assignment shown elsewhere in the app.
/// </summary>
public interface ILoadAssignmentStore
{
    /// <summary>
    /// Compares <paramref name="snapshot"/> against the most recent stored row for the same
    /// <see cref="ObservedAssignment.LoadId"/>; inserts a new row only when something differs (or
    /// none exists yet) so re-viewing an unchanged load never bloats the table. Never throws —
    /// callers treat capture as best-effort.
    /// </summary>
    void CaptureIfChanged(ObservedAssignment snapshot, DateTimeOffset now);

    /// <summary>History for one load, newest first.</summary>
    IReadOnlyList<LoadAssignmentRecord> ListForLoad(string loadId, int max);

    /// <summary>Most recent snapshot across loads, newest first — the reporting/export listing.</summary>
    IReadOnlyList<LoadAssignmentRecord> ListRecent(int max);
}

/// <inheritdoc cref="ILoadAssignmentStore"/>
public sealed class EfLoadAssignmentStore(AppDbContext db) : ILoadAssignmentStore
{
    public void CaptureIfChanged(ObservedAssignment snapshot, DateTimeOffset now)
    {
        // AsEnumerable() before ordering: SQLite's EF provider cannot translate ORDER BY over a
        // DateTimeOffset column to SQL (same constraint EfAssignmentAuditStore.ForLoad already works
        // around). The Where() still runs server-side; only the ordering/first-pick happens in
        // memory, scoped to one load's (typically small) history.
        var latest = db.LoadAssignments
            .Where(a => a.LoadId == snapshot.LoadId)
            .AsEnumerable()
            .OrderByDescending(a => a.CapturedAt)
            .FirstOrDefault();

        if (latest is not null && IsUnchanged(latest, snapshot))
        {
            return;
        }

        db.LoadAssignments.Add(new LoadAssignmentRecord
        {
            Id = Guid.NewGuid().ToString("n"),
            LoadId = snapshot.LoadId,
            LoadNumber = snapshot.LoadNumber,
            TripId = snapshot.TripId,
            Status = snapshot.Status,
            CarrierId = snapshot.CarrierId,
            CarrierName = snapshot.CarrierName,
            Driver1Id = snapshot.Driver1Id,
            Driver1Name = snapshot.Driver1Name,
            Driver2Id = snapshot.Driver2Id,
            Driver2Name = snapshot.Driver2Name,
            OwnerOperatorId = snapshot.OwnerOperatorId,
            OwnerOperatorName = snapshot.OwnerOperatorName,
            TruckId = snapshot.TruckId,
            TrailerId = snapshot.TrailerId,
            DispatcherId = snapshot.DispatcherId,
            DispatchedBy = snapshot.DispatchedBy,
            CarrierAssignedAt = snapshot.CarrierAssignedAt,
            CapturedAt = now,
        });

        db.SaveChanges();
    }

    private static bool IsUnchanged(LoadAssignmentRecord latest, ObservedAssignment snapshot) =>
        latest.TripId == snapshot.TripId
        && latest.Status == snapshot.Status
        && latest.CarrierId == snapshot.CarrierId
        && latest.CarrierName == snapshot.CarrierName
        && latest.Driver1Id == snapshot.Driver1Id
        && latest.Driver1Name == snapshot.Driver1Name
        && latest.Driver2Id == snapshot.Driver2Id
        && latest.Driver2Name == snapshot.Driver2Name
        && latest.OwnerOperatorId == snapshot.OwnerOperatorId
        && latest.OwnerOperatorName == snapshot.OwnerOperatorName
        && latest.TruckId == snapshot.TruckId
        && latest.TrailerId == snapshot.TrailerId
        && latest.DispatcherId == snapshot.DispatcherId
        && latest.DispatchedBy == snapshot.DispatchedBy
        && latest.CarrierAssignedAt == snapshot.CarrierAssignedAt;

    public IReadOnlyList<LoadAssignmentRecord> ListForLoad(string loadId, int max) =>
        db.LoadAssignments.AsNoTracking()
            .Where(a => a.LoadId == loadId)
            .AsEnumerable()
            .OrderByDescending(a => a.CapturedAt)
            .Take(Math.Max(1, max))
            .ToList();

    // Unscoped across all loads: AsEnumerable() (the SQLite DateTimeOffset-ordering workaround, see
    // CaptureIfChanged) pulls the whole table into memory before ordering/limiting, on every
    // provider this code runs against — Take() cannot push down once the query is client-side.
    // Accepted for a moderate-volume internal reporting table; if this table grows large enough for
    // that to matter, the fix is a provider-specific ORDER BY (or a sortable non-DateTimeOffset
    // column) rather than working around it here.
    public IReadOnlyList<LoadAssignmentRecord> ListRecent(int max) =>
        db.LoadAssignments.AsNoTracking()
            .AsEnumerable()
            .OrderByDescending(a => a.CapturedAt)
            .Take(Math.Max(1, max))
            .ToList();
}
