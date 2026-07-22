using LtlTool.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LtlTool.Api.Features.Ltl.YardIngestion;

/// <summary>Outcome of an idempotent append.</summary>
public enum YardAppendStatus
{
    /// <summary>Newly accepted and persisted (HTTP 202).</summary>
    Accepted,

    /// <summary>An event with the same dedupe key was already processed (HTTP 200) — nothing re-written.</summary>
    Duplicate,
}

/// <summary>Append result: the status plus the current projection (if the record has scheduler input).</summary>
public sealed record YardAppendResult(YardAppendStatus Status, YardScheduleInput? Projection);

/// <summary>Filter for the scheduler-facing projection listing.</summary>
public sealed record YardScheduleInputQuery(
    ScheduleReadiness? Readiness = null,
    ScheduleHoldState? HoldState = null,
    string? SourceRecordType = null,
    string? YardLocationId = null,
    bool? SchedulableOnly = null,
    int Max = 200);

/// <summary>
/// Durable persistence for the Yard→LTL ingestion pipeline: an append-only event inbox plus the
/// normalized scheduler projection derived from it. Idempotent on the dedupe key; every append
/// deterministically rebuilds the affected projection from the full event log, so out-of-order
/// delivery and replay produce identical results. Backed by <see cref="AppDbContext"/>.
/// </summary>
public interface IYardEventStore
{
    /// <summary>
    /// Idempotently appends one event. A duplicate dedupe key is a no-op that returns
    /// <see cref="YardAppendStatus.Duplicate"/> with the current projection. A new event is persisted
    /// and its projection rebuilt in a single atomic unit of work.
    /// </summary>
    YardAppendResult Append(YardEventRecord evt, DateTimeOffset receivedAt);

    /// <summary>The scheduler projection for one source record, or null when none exists yet.</summary>
    YardScheduleInput? GetProjection(string sourceSystem, string sourceRecordType, string sourceRecordId);

    /// <summary>Scheduler projections matching the filter, most-recently-updated first.</summary>
    IReadOnlyList<YardScheduleInput> QueryProjections(YardScheduleInputQuery query);

    /// <summary>Recent inbox events, newest first (audit view).</summary>
    IReadOnlyList<YardEventRecord> ListEvents(int max);

    /// <summary>All inbox events for one source record, oldest occurrence first (audit / replay view).</summary>
    IReadOnlyList<YardEventRecord> ListEventsForRecord(string sourceSystem, string sourceRecordType, string sourceRecordId);

    /// <summary>
    /// Rebuilds the projection for one source record from its stored events without accepting a new
    /// event. Returns the rebuilt projection (or null if the record has no freight-affecting events).
    /// </summary>
    YardScheduleInput? ReplayRecord(string sourceSystem, string sourceRecordType, string sourceRecordId);
}

/// <inheritdoc cref="IYardEventStore"/>
public sealed class EfYardEventStore(AppDbContext db) : IYardEventStore
{
    public YardAppendResult Append(YardEventRecord evt, DateTimeOffset receivedAt)
    {
        if (db.YardEvents.AsNoTracking().Any(e => e.DedupeKey == evt.DedupeKey))
            return new YardAppendResult(YardAppendStatus.Duplicate, LoadProjection(evt));

        evt.ReceivedAt = receivedAt;
        // Store-assigned monotonic ordinal so the whole append is a single atomic SaveChanges. Used
        // only as an occurrence-time tie-breaker during the projection rebuild.
        evt.Sequence = (db.YardEvents.Max(e => (long?)e.Sequence) ?? 0L) + 1L;
        db.YardEvents.Add(evt);

        RebuildProjection(evt.SourceSystem, evt.SourceRecordType, evt.SourceRecordId, includeUnsaved: evt);

        try
        {
            db.SaveChanges();
        }
        catch (DbUpdateException)
        {
            // A concurrent delivery of the same dedupe key won the unique-key race; treat as duplicate.
            db.ChangeTracker.Clear();
            return new YardAppendResult(YardAppendStatus.Duplicate, LoadProjection(evt));
        }

        return new YardAppendResult(YardAppendStatus.Accepted, LoadProjection(evt));
    }

    public YardScheduleInput? GetProjection(string sourceSystem, string sourceRecordType, string sourceRecordId)
    {
        var id = YardEventProjectionBuilder.ProjectionId(sourceSystem, sourceRecordType, sourceRecordId);
        return db.YardScheduleInputs.AsNoTracking().FirstOrDefault(p => p.Id == id);
    }

    public IReadOnlyList<YardScheduleInput> QueryProjections(YardScheduleInputQuery query)
    {
        var rows = db.YardScheduleInputs.AsNoTracking().AsEnumerable();

        if (query.Readiness is { } readiness)
            rows = rows.Where(p => p.Readiness == readiness.ToString());
        if (query.HoldState is { } hold)
            rows = rows.Where(p => p.HoldState == hold.ToString());
        if (!string.IsNullOrWhiteSpace(query.SourceRecordType))
            rows = rows.Where(p => string.Equals(p.SourceRecordType, query.SourceRecordType, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(query.YardLocationId))
            rows = rows.Where(p => string.Equals(p.YardLocationId, query.YardLocationId, StringComparison.OrdinalIgnoreCase));
        if (query.SchedulableOnly == true)
            rows = rows.Where(p => p.HoldState != ScheduleHoldState.Cancelled.ToString()
                                   && p.HoldState != ScheduleHoldState.Held.ToString());

        return rows
            .OrderByDescending(p => p.UpdatedAt)
            .Take(Math.Clamp(query.Max, 1, 1000))
            .ToArray();
    }

    public IReadOnlyList<YardEventRecord> ListEvents(int max) =>
        db.YardEvents.AsNoTracking()
            .AsEnumerable()
            .OrderByDescending(e => e.Sequence)
            .Take(Math.Clamp(max, 1, 1000))
            .ToArray();

    public IReadOnlyList<YardEventRecord> ListEventsForRecord(
        string sourceSystem, string sourceRecordType, string sourceRecordId) =>
        RecordEvents(sourceSystem, sourceRecordType, sourceRecordId)
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.Sequence)
            .ToArray();

    public YardScheduleInput? ReplayRecord(string sourceSystem, string sourceRecordType, string sourceRecordId)
    {
        RebuildProjection(sourceSystem, sourceRecordType, sourceRecordId, includeUnsaved: null);
        db.SaveChanges();
        return GetProjection(sourceSystem, sourceRecordType, sourceRecordId);
    }

    /// <summary>
    /// Recomputes and upserts the projection for one source record from its stored events (plus an
    /// optional not-yet-saved event). Does not call SaveChanges — the caller commits, so the event
    /// insert and the projection upsert land in one transaction.
    /// </summary>
    private void RebuildProjection(
        string sourceSystem, string sourceRecordType, string sourceRecordId, YardEventRecord? includeUnsaved)
    {
        var events = RecordEvents(sourceSystem, sourceRecordType, sourceRecordId).ToList();
        if (includeUnsaved is not null)
            events.Add(includeUnsaved);

        var built = YardEventProjectionBuilder.Build(events);
        var id = YardEventProjectionBuilder.ProjectionId(sourceSystem, sourceRecordType, sourceRecordId);
        var current = db.YardScheduleInputs.FirstOrDefault(p => p.Id == id);

        if (built is null)
        {
            // No freight-affecting events (administrative-only record) — nothing to project.
            return;
        }

        if (current is null)
        {
            db.YardScheduleInputs.Add(built);
        }
        else
        {
            CopyInto(current, built);
        }
    }

    private YardScheduleInput? LoadProjection(YardEventRecord evt) =>
        GetProjection(evt.SourceSystem, evt.SourceRecordType, evt.SourceRecordId);

    private List<YardEventRecord> RecordEvents(string sourceSystem, string sourceRecordType, string sourceRecordId) =>
        db.YardEvents.AsNoTracking()
            .Where(e => e.SourceSystem == sourceSystem
                        && e.SourceRecordType == sourceRecordType
                        && e.SourceRecordId == sourceRecordId)
            .ToList();

    private static void CopyInto(YardScheduleInput target, YardScheduleInput source)
    {
        target.YardLocationId = source.YardLocationId;
        target.SchedulerEligible = source.SchedulerEligible;
        target.Readiness = source.Readiness;
        target.Completeness = source.Completeness;
        target.HoldState = source.HoldState;
        target.DockCompleted = source.DockCompleted;
        target.SecurityCleared = source.SecurityCleared;
        target.HasOpenException = source.HasOpenException;
        target.LatestOccurredAt = source.LatestOccurredAt;
        target.LatestEventType = source.LatestEventType;
        target.LatestEventId = source.LatestEventId;
        target.EventCount = source.EventCount;
        target.TruckId = source.TruckId;
        target.TrailerId = source.TrailerId;
        target.DockId = source.DockId;
        target.WeightLbs = source.WeightLbs;
        target.LengthInches = source.LengthInches;
        target.WidthInches = source.WidthInches;
        target.HeightInches = source.HeightInches;
        target.PieceCount = source.PieceCount;
        target.OriginLocationId = source.OriginLocationId;
        target.DestinationLocationId = source.DestinationLocationId;
        target.AppointmentAt = source.AppointmentAt;
        target.RelationshipType = source.RelationshipType;
        target.ParentSourceRecordId = source.ParentSourceRecordId;
        target.RelatedRecordIdsJson = source.RelatedRecordIdsJson;
        target.CreatedAt = source.CreatedAt;
        target.UpdatedAt = source.UpdatedAt;
    }
}
