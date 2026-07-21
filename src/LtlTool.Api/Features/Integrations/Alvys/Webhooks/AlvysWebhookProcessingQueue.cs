using System.Threading.Channels;

namespace LtlTool.Api.Features.Integrations.Alvys.Webhooks;

/// <summary>
/// Hands off a freshly-received webhook event id from the HTTP receiver to the background processor.
/// The receiver enqueues and acks immediately; processing never blocks the ack, so a slow read-model
/// refresh can never make Alvys auto-disable the subscription. The queue is bounded and drops nothing
/// silently — a full queue makes the enqueue wait rather than lose an event, and even if an enqueue
/// were skipped the durable event row is already persisted and can be reprocessed.
/// </summary>
public interface IAlvysWebhookProcessingQueue
{
    /// <summary>Enqueue a received event id for background processing. Non-blocking in practice.</summary>
    ValueTask EnqueueAsync(string eventId, CancellationToken ct = default);

    /// <summary>The reader the background processor drains.</summary>
    ChannelReader<string> Reader { get; }
}

/// <inheritdoc cref="IAlvysWebhookProcessingQueue"/>
public sealed class AlvysWebhookProcessingQueue : IAlvysWebhookProcessingQueue
{
    private readonly Channel<string> _channel =
        Channel.CreateBounded<string>(new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

    public ValueTask EnqueueAsync(string eventId, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(eventId, ct);

    public ChannelReader<string> Reader => _channel.Reader;
}
