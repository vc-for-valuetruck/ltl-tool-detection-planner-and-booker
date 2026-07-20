using System.Collections.Concurrent;

namespace LtlTool.Api.Features.Ltl.Notifications;

/// <summary>
/// Store for fired notification events. Abstracted so a production deployment can swap the
/// in-memory default for a durable, queryable store (e.g. EF Core, reusing the outbox pattern
/// from PR #19) without touching the engine or controller.
/// </summary>
public interface INotificationStore
{
    /// <summary>
    /// Records a fired event iff its idempotency key has not been seen. Returns true when newly
    /// added, false when the key already exists (a re-poll / restart of the same real event).
    /// </summary>
    bool TryAdd(NotificationEvent evt);

    /// <summary>Whether an event with this idempotency key has already fired.</summary>
    bool Contains(string idempotencyKey);

    /// <summary>The most recent events, newest first, capped at <paramref name="max"/>.</summary>
    IReadOnlyList<NotificationEvent> Recent(int max);

    /// <summary>Total events fired since startup.</summary>
    int Count { get; }
}

/// <summary>
/// Thread-safe in-memory <see cref="INotificationStore"/>. Matches the same posture as
/// <c>InMemoryConsolidationAuditStore</c>: suitable for the first slice and local/UAT/demo; not
/// durable across restarts. The idempotency-key set is the restart-safety backstop within a
/// single process lifetime — a durable store extends that guarantee across restarts.
/// </summary>
public sealed class InMemoryNotificationStore : INotificationStore
{
    private readonly ConcurrentDictionary<string, byte> _keys = new(StringComparer.Ordinal);
    private readonly LinkedList<NotificationEvent> _events = new();
    private readonly object _gate = new();

    // Soft retention bound applied when trimming; kept generous so the demo feed never silently
    // drops a trigger the presenter just generated.
    private const int MaxRetained = 1000;

    public bool TryAdd(NotificationEvent evt)
    {
        if (!_keys.TryAdd(evt.IdempotencyKey, 0))
        {
            return false;
        }

        lock (_gate)
        {
            _events.AddFirst(evt);
            while (_events.Count > MaxRetained)
            {
                var oldest = _events.Last!.Value;
                _events.RemoveLast();
                _keys.TryRemove(oldest.IdempotencyKey, out _);
            }
        }
        return true;
    }

    public bool Contains(string idempotencyKey) => _keys.ContainsKey(idempotencyKey);

    public IReadOnlyList<NotificationEvent> Recent(int max)
    {
        if (max <= 0) max = 50;
        lock (_gate)
        {
            return _events.Take(max).ToArray();
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _events.Count;
            }
        }
    }
}
