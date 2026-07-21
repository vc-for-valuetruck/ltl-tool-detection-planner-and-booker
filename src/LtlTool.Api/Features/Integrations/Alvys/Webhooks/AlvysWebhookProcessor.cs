using Microsoft.Extensions.DependencyInjection;

namespace LtlTool.Api.Features.Integrations.Alvys.Webhooks;

/// <summary>
/// Drains the webhook processing queue off the request thread and advances each received event to
/// <see cref="AlvysWebhookProcessingState.Processed"/> or <see cref="AlvysWebhookProcessingState.Failed"/>.
/// Processing updates the per-load freshness marker consumed by billing readiness / exceptions; it does
/// <b>not</b> fetch or store any operational value from the payload (Alvys stays authoritative — the
/// marker only records that a load changed and when). A failure on one event is isolated: it is recorded
/// on that event and the processor keeps draining, so one bad payload never stalls the pipeline.
/// </summary>
public sealed class AlvysWebhookProcessor(
    IAlvysWebhookProcessingQueue queue,
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<AlvysWebhookProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var eventId in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                Process(eventId);
            }
            catch (Exception ex)
            {
                // Never let one event take down the processor loop. Status only — no payload/body logged.
                logger.LogError(ex, "Alvys webhook processing failed for event {EventId}.", eventId);
                TryMarkFailed(eventId, ex.Message);
            }
        }
    }

    private void Process(string eventId)
    {
        // A fresh DI scope per event so the scoped DbContext is short-lived (the processor is a singleton).
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAlvysWebhookStore>();

        var evt = store.Get(eventId);
        if (evt is null)
        {
            logger.LogWarning("Alvys webhook event {EventId} vanished before processing.", eventId);
            return;
        }

        var loadNumber = evt.LoadNumber ?? AlvysWebhookPayload.TryExtractLoadNumber(evt.RawBody);
        store.MarkProcessed(eventId, loadNumber, evt.EventType, clock.GetUtcNow());
    }

    private void TryMarkFailed(string eventId, string error)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<IAlvysWebhookStore>()
                .MarkFailed(eventId, error, clock.GetUtcNow());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Alvys webhook failure could not be recorded for event {EventId}.", eventId);
        }
    }
}
