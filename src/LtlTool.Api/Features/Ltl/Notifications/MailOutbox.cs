using System.Collections.Concurrent;

namespace LtlTool.Api.Features.Ltl.Notifications;

/// <summary>
/// One outbox entry per real-world notification, keyed by the event's idempotency key. Records the
/// resolved delivery state, attempt count, last detail and timestamp so a retry (the dock "retry
/// chip" or a re-poll) never re-sends an email that already landed, and so ops can read the last send
/// result per channel. Stores no secrets and no message body — recipient addresses are already
/// server-side config.
/// </summary>
public sealed class MailOutboxEntry
{
    public required string IdempotencyKey { get; init; }
    public required NotificationDeliveryState State { get; set; }
    public int AttemptCount { get; set; }
    public string? Detail { get; set; }
    public IReadOnlyList<string> Recipients { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Idempotent store for mail sends. A <see cref="NotificationDeliveryState.Delivered"/> entry is
/// terminal: re-sending the same key is a no-op replay (no duplicate email). The seam is abstracted so
/// the state machine is unit-testable; the in-memory implementation matches the same posture as the
/// other Phase 6 stores (swap for an EF-backed store when durability across instances is needed).
/// </summary>
public interface IMailOutbox
{
    /// <summary>The entry for a key, or null when this event has never been attempted.</summary>
    MailOutboxEntry? Get(string idempotencyKey);

    /// <summary>Inserts or replaces the entry for its key.</summary>
    void Save(MailOutboxEntry entry);

    /// <summary>The most recently updated entry across all keys, or null. Powers the ops status read.</summary>
    MailOutboxEntry? MostRecent();
}

/// <summary>Thread-safe in-memory <see cref="IMailOutbox"/>. Registered as a singleton.</summary>
public sealed class InMemoryMailOutbox : IMailOutbox
{
    private readonly ConcurrentDictionary<string, MailOutboxEntry> _byKey = new();

    public MailOutboxEntry? Get(string idempotencyKey) =>
        _byKey.TryGetValue(idempotencyKey, out var e) ? Clone(e) : null;

    public void Save(MailOutboxEntry entry) => _byKey[entry.IdempotencyKey] = Clone(entry);

    public MailOutboxEntry? MostRecent()
    {
        var latest = _byKey.Values
            .OrderByDescending(e => e.UpdatedAt)
            .FirstOrDefault();
        return latest is null ? null : Clone(latest);
    }

    private static MailOutboxEntry Clone(MailOutboxEntry e) => new()
    {
        IdempotencyKey = e.IdempotencyKey,
        State = e.State,
        AttemptCount = e.AttemptCount,
        Detail = e.Detail,
        Recipients = e.Recipients.ToArray(),
        UpdatedAt = e.UpdatedAt,
    };
}
