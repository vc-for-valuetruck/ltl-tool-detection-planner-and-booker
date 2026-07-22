using System.Threading.Channels;

namespace LtlTool.Api.Features.Integrations.Yard.Webhooks;

/// <summary>
/// Hands off a freshly-received Yard webhook event id from the HTTP receiver to the background
/// processor. The receiver enqueues and acks immediately; processing never blocks the ack. The queue is
/// bounded and drops nothing silently — a full queue makes the enqueue wait rather than lose an event,
/// and even if an enqueue were skipped the durable event row is already persisted and can be reprocessed.
/// </summary>
public interface IYardWebhookProcessingQueue
{
    /// <summary>Enqueue a received event id for background processing. Non-blocking in practice.</summary>
    ValueTask EnqueueAsync(string eventId, CancellationToken ct = default);

    /// <summary>The reader the background processor drains.</summary>
    ChannelReader<string> Reader { get; }
}

/// <inheritdoc cref="IYardWebhookProcessingQueue"/>
public sealed class YardWebhookProcessingQueue : IYardWebhookProcessingQueue
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
