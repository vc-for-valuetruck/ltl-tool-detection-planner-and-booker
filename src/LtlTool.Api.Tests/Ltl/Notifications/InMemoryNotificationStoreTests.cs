using LtlTool.Api.Features.Ltl.Notifications;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Notifications;

/// <summary>Behavior tests for the in-memory notification feed store: idempotency, newest-first
/// ordering and the retention bound.</summary>
public sealed class InMemoryNotificationStoreTests
{
    private static NotificationEvent Event(string key) => new()
    {
        Id = Guid.NewGuid().ToString("n"),
        IdempotencyKey = key,
        Stage = NotificationStage.ConsolidationPlanCreated,
        Title = key,
        Summary = "s",
        OccurredAt = DateTimeOffset.UtcNow,
        FiredAt = DateTimeOffset.UtcNow,
        Deliveries = [],
    };

    [Fact]
    public void TryAdd_dedupes_by_idempotency_key()
    {
        var store = new InMemoryNotificationStore();

        Assert.True(store.TryAdd(Event("k")));
        Assert.False(store.TryAdd(Event("k")));
        Assert.Equal(1, store.Count);
        Assert.True(store.Contains("k"));
    }

    [Fact]
    public void Recent_returns_newest_first_capped_at_max()
    {
        var store = new InMemoryNotificationStore();
        store.TryAdd(Event("k1"));
        store.TryAdd(Event("k2"));
        store.TryAdd(Event("k3"));

        var recent = store.Recent(2);

        Assert.Equal(2, recent.Count);
        Assert.Equal("k3", recent[0].Title);
        Assert.Equal("k2", recent[1].Title);
    }

    [Fact]
    public void Recent_defaults_when_max_non_positive()
    {
        var store = new InMemoryNotificationStore();
        store.TryAdd(Event("k1"));

        Assert.Single(store.Recent(0));
    }
}
