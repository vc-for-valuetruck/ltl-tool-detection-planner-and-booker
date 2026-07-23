using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace LtlTool.Api.Features.Integrations.Yard.Webhooks;

/// <summary>
/// Drains the Yard webhook processing queue off the request thread and applies each event's side effect,
/// then advances it to <see cref="YardWebhookProcessingState.Processed"/> or
/// <see cref="YardWebhookProcessingState.Failed"/>. Side effects per event type (shared contract §"LTL
/// receiver behavior"): <c>TruckArrived</c> invalidates the presence cache; <c>LoadReleased</c>
/// invalidates and fans out to the dock; <c>LtlDraftCreated</c> persists a yard-originated opportunity
/// and fans out. A failure on one event is isolated: recorded on that event, and the loop keeps draining.
/// </summary>
public sealed class YardWebhookProcessor(
    IYardWebhookProcessingQueue queue,
    IServiceScopeFactory scopeFactory,
    IYardPresenceClient presence,
    IHubContext<YardEventsHub> hub,
    TimeProvider clock,
    ILogger<YardWebhookProcessor> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var eventId in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(eventId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Never let one event take down the processor loop. Status only — no payload/body logged.
                logger.LogError(ex, "Yard webhook processing failed for event {EventId}.", eventId);
                TryMarkFailed(eventId, ex.Message);
            }
        }
    }

    private async Task ProcessAsync(string eventId, CancellationToken ct)
    {
        // A fresh DI scope per event so the scoped DbContext is short-lived (the processor is a singleton).
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IYardWebhookStore>();

        var evt = store.Get(eventId);
        if (evt is null)
        {
            logger.LogWarning("Yard webhook event {EventId} vanished before processing.", eventId);
            return;
        }

        var envelope = YardWebhookPayload.TryParse(evt.RawBody);

        switch (evt.EventType)
        {
            case YardEventTypes.TruckArrived:
                presence.InvalidatePresence(evt.TractorId, evt.TrailerId, evt.DriverId);
                break;

            case YardEventTypes.LoadReleased:
                presence.InvalidatePresence(evt.TractorId, evt.TrailerId, evt.DriverId);
                await hub.Clients.All.SendAsync(
                    YardEventsHub.LoadReleasedMethod,
                    new { evt.EventId, evt.YardCode, evt.TractorId, evt.TrailerId, evt.DriverId, loadIds = envelope?.LoadIds ?? [] },
                    ct);
                break;

            case YardEventTypes.LtlDraftCreated:
                var view = PersistOpportunity(store, evt, envelope, clock.GetUtcNow());
                if (view is not null)
                    await hub.Clients.All.SendAsync(YardEventsHub.OpportunityCreatedMethod, view, ct);
                break;

            default:
                logger.LogInformation(
                    "Yard webhook event {EventId} has unhandled type {EventType}; recorded only.",
                    eventId, evt.EventType);
                break;
        }

        store.MarkProcessed(eventId, clock.GetUtcNow());
    }

    private static YardOpportunityView? PersistOpportunity(
        IYardWebhookStore store, YardWebhookEvent evt, YardWebhookEnvelope? envelope, DateTimeOffset now)
    {
        var draft = envelope?.Draft;
        if (draft?.DraftId is null)
            return null;

        var opportunity = new YardLtlOpportunity
        {
            Id = $"yopp-{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString()[..8]}",
            EventId = evt.EventId,
            DraftId = draft.DraftId,
            YardCode = evt.YardCode,
            ParentLoadId = draft.ParentLoadId,
            SiblingLoadIdsJson = JsonSerializer.Serialize(draft.SiblingLoadIds ?? [], JsonOptions),
            FreightJson = JsonSerializer.Serialize(draft.Freight ?? [], JsonOptions),
            CreatedByStation = draft.CreatedByStation,
            ScannedAt = draft.ScannedAt,
            ReceivedAt = now,
        };

        store.UpsertOpportunity(opportunity);
        return YardOpportunityView.From(opportunity);
    }

    private void TryMarkFailed(string eventId, string error)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<IYardWebhookStore>()
                .MarkFailed(eventId, error, clock.GetUtcNow());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Yard webhook failure could not be recorded for event {EventId}.", eventId);
        }
    }
}
