using LtlTool.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LtlTool.Api.Features.Integrations.Alvys.Webhooks;

/// <summary>
/// Durable persistence for received webhook events and per-load freshness markers. The event id is the
/// primary key, so a duplicate delivery is rejected on insert (at-least-once delivery is made
/// idempotent). Backed by <see cref="AppDbContext"/>; scoped to the request/background scope.
/// </summary>
public interface IAlvysWebhookStore
{
    /// <summary>
    /// Inserts a received event. Returns false when the event id already exists (a duplicate delivery),
    /// in which case nothing is written and the caller should still ack.
    /// </summary>
    bool TryInsertReceived(AlvysWebhookEvent evt);

    /// <summary>The most-recent-first received events, capped at <paramref name="limit"/>.</summary>
    IReadOnlyList<AlvysWebhookEvent> ListRecent(int limit);

    /// <summary>Total number of received events (lifetime).</summary>
    int Count();

    /// <summary>A single event by id (null when missing).</summary>
    AlvysWebhookEvent? Get(string eventId);

    /// <summary>Marks an event processed and upserts the load freshness marker in one unit of work.</summary>
    void MarkProcessed(string eventId, string? loadNumber, string eventType, DateTimeOffset processedAt);

    /// <summary>Marks an event failed with a bounded error detail.</summary>
    void MarkFailed(string eventId, string error, DateTimeOffset failedAt);

    /// <summary>The freshness marker for a load (null when the tool has seen no change for it).</summary>
    LoadFreshnessRecord? GetFreshness(string loadNumber);
}

/// <inheritdoc cref="IAlvysWebhookStore"/>
public sealed class EfAlvysWebhookStore(AppDbContext db) : IAlvysWebhookStore
{
    private const int MaxErrorLength = 2048;

    public bool TryInsertReceived(AlvysWebhookEvent evt)
    {
        if (db.AlvysWebhookEvents.Any(e => e.EventId == evt.EventId))
            return false;

        db.AlvysWebhookEvents.Add(evt);
        try
        {
            db.SaveChanges();
            return true;
        }
        catch (DbUpdateException)
        {
            // A concurrent delivery of the same event id won the unique-index race; treat as duplicate.
            db.Entry(evt).State = EntityState.Detached;
            return false;
        }
    }

    public IReadOnlyList<AlvysWebhookEvent> ListRecent(int limit) =>
        db.AlvysWebhookEvents.AsNoTracking()
            .AsEnumerable()
            .OrderByDescending(e => e.ReceivedAt)
            .Take(limit)
            .ToArray();

    public int Count() => db.AlvysWebhookEvents.Count();

    public AlvysWebhookEvent? Get(string eventId) =>
        db.AlvysWebhookEvents.AsNoTracking().FirstOrDefault(e => e.EventId == eventId);

    public void MarkProcessed(string eventId, string? loadNumber, string eventType, DateTimeOffset processedAt)
    {
        var evt = db.AlvysWebhookEvents.FirstOrDefault(e => e.EventId == eventId);
        if (evt is null) return;

        evt.ProcessingState = AlvysWebhookProcessingState.Processed;
        evt.ProcessingError = null;
        evt.ProcessedAt = processedAt;

        if (!string.IsNullOrWhiteSpace(loadNumber))
        {
            var freshness = db.LoadFreshness.FirstOrDefault(f => f.LoadNumber == loadNumber);
            if (freshness is null)
            {
                db.LoadFreshness.Add(new LoadFreshnessRecord
                {
                    LoadNumber = loadNumber,
                    LastEventType = eventType,
                    LastEventId = eventId,
                    LastChangedAt = processedAt,
                    ChangeCount = 1,
                });
            }
            else
            {
                freshness.LastEventType = eventType;
                freshness.LastEventId = eventId;
                freshness.LastChangedAt = processedAt;
                freshness.ChangeCount += 1;
            }
        }

        db.SaveChanges();
    }

    public void MarkFailed(string eventId, string error, DateTimeOffset failedAt)
    {
        var evt = db.AlvysWebhookEvents.FirstOrDefault(e => e.EventId == eventId);
        if (evt is null) return;

        evt.ProcessingState = AlvysWebhookProcessingState.Failed;
        evt.ProcessingError = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
        evt.ProcessedAt = failedAt;
        db.SaveChanges();
    }

    public LoadFreshnessRecord? GetFreshness(string loadNumber) =>
        db.LoadFreshness.AsNoTracking().FirstOrDefault(f => f.LoadNumber == loadNumber);
}
