using LtlTool.Api.Features.Integrations.Yard;
using LtlTool.Api.Features.Integrations.Yard.Webhooks;

namespace LtlTool.Api.Tests.Yard;

/// <summary>
/// Configurable in-memory <see cref="IYardPresenceClient"/> for controller/service tests. Defaults to
/// unconfigured (the honest "Yard integration off" shape) so a test opts in to a live presence result.
/// </summary>
internal sealed class FakeYardPresenceClient : IYardPresenceClient
{
    public bool IsConfigured { get; set; }
    public YardPresence? Presence { get; set; }
    public (string? Tractor, string? Trailer, string? Driver)? LastQuery { get; private set; }

    public Task<YardPresence?> GetPresenceAsync(
        string? tractorId, string? trailerId, string? driverId, CancellationToken ct = default)
    {
        LastQuery = (tractorId, trailerId, driverId);
        return Task.FromResult(Presence);
    }

    public void InvalidatePresence(string? tractorId, string? trailerId, string? driverId) { }
}

/// <summary>In-memory <see cref="IYardWebhookStore"/> backed by plain lists, for controller tests.</summary>
internal sealed class FakeYardWebhookStore : IYardWebhookStore
{
    public List<YardWebhookEvent> Events { get; } = [];
    public List<YardLtlOpportunity> Opportunities { get; } = [];

    public bool TryInsertReceived(YardWebhookEvent evt)
    {
        if (Events.Any(e => e.EventId == evt.EventId)) return false;
        Events.Add(evt);
        return true;
    }

    public IReadOnlyList<YardWebhookEvent> ListRecent(int limit) =>
        Events.OrderByDescending(e => e.ReceivedAt).Take(limit).ToArray();

    public int Count() => Events.Count;

    public YardWebhookEvent? Get(string eventId) => Events.FirstOrDefault(e => e.EventId == eventId);

    public void MarkProcessed(string eventId, DateTimeOffset processedAt)
    {
        var evt = Get(eventId);
        if (evt is null) return;
        evt.ProcessingState = YardWebhookProcessingState.Processed;
        evt.ProcessedAt = processedAt;
    }

    public void MarkFailed(string eventId, string error, DateTimeOffset failedAt)
    {
        var evt = Get(eventId);
        if (evt is null) return;
        evt.ProcessingState = YardWebhookProcessingState.Failed;
        evt.ProcessingError = error;
        evt.ProcessedAt = failedAt;
    }

    public void UpsertOpportunity(YardLtlOpportunity opportunity)
    {
        if (Opportunities.Any(o => o.EventId == opportunity.EventId)) return;
        Opportunities.Add(opportunity);
    }

    public IReadOnlyList<YardLtlOpportunity> ListOpportunities(int limit) =>
        Opportunities.OrderByDescending(o => o.ReceivedAt).Take(limit).ToArray();
}
