using LtlTool.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LtlTool.Api.Features.Integrations.Yard.Webhooks;

/// <summary>
/// Durable persistence for received Yard webhook events and the LTL opportunities derived from
/// <c>LtlDraftCreated</c>. The event id is the primary key, so a duplicate delivery is rejected on
/// insert (at-least-once delivery is made idempotent). Backed by <see cref="AppDbContext"/>.
/// </summary>
public interface IYardWebhookStore
{
    /// <summary>
    /// Inserts a received event. Returns false when the event id already exists (a duplicate delivery),
    /// in which case nothing is written and the caller should still ack.
    /// </summary>
    bool TryInsertReceived(YardWebhookEvent evt);

    /// <summary>The most-recent-first received events, capped at <paramref name="limit"/>.</summary>
    IReadOnlyList<YardWebhookEvent> ListRecent(int limit);

    /// <summary>Total number of received events (lifetime).</summary>
    int Count();

    /// <summary>A single event by id (null when missing).</summary>
    YardWebhookEvent? Get(string eventId);

    /// <summary>Marks an event processed.</summary>
    void MarkProcessed(string eventId, DateTimeOffset processedAt);

    /// <summary>Marks an event failed with a bounded error detail.</summary>
    void MarkFailed(string eventId, string error, DateTimeOffset failedAt);

    /// <summary>
    /// Persists a yard-originated LTL opportunity. Idempotent on the source event id — a re-processed
    /// event never creates a duplicate card.
    /// </summary>
    void UpsertOpportunity(YardLtlOpportunity opportunity);

    /// <summary>The most-recent-first yard-originated opportunities, capped at <paramref name="limit"/>.</summary>
    IReadOnlyList<YardLtlOpportunity> ListOpportunities(int limit);
}

/// <inheritdoc cref="IYardWebhookStore"/>
public sealed class EfYardWebhookStore(AppDbContext db) : IYardWebhookStore
{
    private const int MaxErrorLength = 2048;

    public bool TryInsertReceived(YardWebhookEvent evt)
    {
        if (db.YardWebhookEvents.Any(e => e.EventId == evt.EventId))
            return false;

        db.YardWebhookEvents.Add(evt);
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

    public IReadOnlyList<YardWebhookEvent> ListRecent(int limit) =>
        db.YardWebhookEvents.AsNoTracking()
            .AsEnumerable()
            .OrderByDescending(e => e.ReceivedAt)
            .Take(limit)
            .ToArray();

    public int Count() => db.YardWebhookEvents.Count();

    public YardWebhookEvent? Get(string eventId) =>
        db.YardWebhookEvents.AsNoTracking().FirstOrDefault(e => e.EventId == eventId);

    public void MarkProcessed(string eventId, DateTimeOffset processedAt)
    {
        var evt = db.YardWebhookEvents.FirstOrDefault(e => e.EventId == eventId);
        if (evt is null) return;

        evt.ProcessingState = YardWebhookProcessingState.Processed;
        evt.ProcessingError = null;
        evt.ProcessedAt = processedAt;
        db.SaveChanges();
    }

    public void MarkFailed(string eventId, string error, DateTimeOffset failedAt)
    {
        var evt = db.YardWebhookEvents.FirstOrDefault(e => e.EventId == eventId);
        if (evt is null) return;

        evt.ProcessingState = YardWebhookProcessingState.Failed;
        evt.ProcessingError = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
        evt.ProcessedAt = failedAt;
        db.SaveChanges();
    }

    public void UpsertOpportunity(YardLtlOpportunity opportunity)
    {
        if (db.YardLtlOpportunities.Any(o => o.EventId == opportunity.EventId))
            return;

        db.YardLtlOpportunities.Add(opportunity);
        try
        {
            db.SaveChanges();
        }
        catch (DbUpdateException)
        {
            db.Entry(opportunity).State = EntityState.Detached;
        }
    }

    public IReadOnlyList<YardLtlOpportunity> ListOpportunities(int limit) =>
        db.YardLtlOpportunities.AsNoTracking()
            .AsEnumerable()
            .OrderByDescending(o => o.ReceivedAt)
            .Take(limit)
            .ToArray();
}
