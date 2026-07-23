using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.Notifications;

/// <summary>
/// Describes a real-world workflow event the engine has detected, before it is fanned out to
/// channels. Kept separate from <see cref="NotificationEvent"/> so detection code never has to
/// know about channels, idempotency storage, or fire timestamps.
/// </summary>
public sealed class NotificationTrigger
{
    public required NotificationStage Stage { get; init; }

    /// <summary>Stable identity of the underlying event for idempotency (e.g. parent load id).</summary>
    public required string SourceKey { get; init; }

    public required string Title { get; init; }
    public required string Summary { get; init; }
    public string? LoadId { get; init; }
    public string? LoadNumber { get; init; }
    public string? PlanId { get; init; }
    public string? LinkPath { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
}

/// <summary>
/// Turns a detected <see cref="NotificationTrigger"/> into a fired, deduped, fanned-out
/// <see cref="NotificationEvent"/>. Idempotency is enforced against <see cref="INotificationStore"/>
/// so the same real-world event never notifies twice across re-polls or restarts (within the
/// store's durability window). Recipient resolution falls back to per-stage defaults so the feed
/// always shows who should be aligned even before an operator customises the recipient groups.
/// </summary>
public sealed class NotificationDispatcher(
    IEnumerable<INotificationChannel> channels,
    INotificationStore store,
    IOptions<NotificationOptions> options,
    TimeProvider clock)
{
    private readonly IReadOnlyList<INotificationChannel> _channels = channels.ToArray();
    private readonly NotificationOptions _options = options.Value;

    /// <summary>
    /// Fires the trigger. Returns the stored event, or null when it was a duplicate (already fired).
    /// When <paramref name="inAppOnly"/> is true, external channels (Teams/email) are skipped
    /// entirely regardless of recipient config — used by the AR digest, which is an in-app-only
    /// summary and must never trigger a Graph/email send.
    /// </summary>
    public async Task<NotificationEvent?> DispatchAsync(
        NotificationTrigger trigger, CancellationToken ct, bool inAppOnly = false)
    {
        var key = BuildIdempotencyKey(trigger);
        if (store.Contains(key))
        {
            return null;
        }

        var recipients = ResolveRecipients(trigger.Stage);
        var now = clock.GetUtcNow();

        var deliveries = new List<NotificationDelivery>();

        // In-app is always-on: the feed shows the whole group so everyone can self-serve, even
        // recipients configured only for an external channel.
        var inApp = _channels.FirstOrDefault(c => c.Kind == NotificationChannelKind.InApp);
        if (inApp is not null)
        {
            deliveries.Add(await inApp.SendAsync(Draft(trigger, now, key), recipients, ct));
        }

        // External channels only fan out to the recipients explicitly configured for them.
        // Skipped wholesale for in-app-only triggers (e.g. the AR digest).
        foreach (var kind in inAppOnly
            ? Array.Empty<NotificationChannelKind>()
            : new[] { NotificationChannelKind.Teams, NotificationChannelKind.Email })
        {
            var targeted = recipients.Where(r => r.Channel == kind).ToArray();
            if (targeted.Length == 0) continue;

            var channel = _channels.FirstOrDefault(c => c.Kind == kind);
            if (channel is null) continue;

            deliveries.Add(await channel.SendAsync(Draft(trigger, now, key), targeted, ct));
        }

        var evt = new NotificationEvent
        {
            Id = Guid.NewGuid().ToString("n"),
            IdempotencyKey = key,
            Stage = trigger.Stage,
            Title = trigger.Title,
            Summary = trigger.Summary,
            LoadId = trigger.LoadId,
            LoadNumber = trigger.LoadNumber,
            PlanId = trigger.PlanId,
            LinkPath = trigger.LinkPath,
            OccurredAt = trigger.OccurredAt,
            FiredAt = now,
            Deliveries = deliveries,
        };

        // Race-safe: if a concurrent poll stored the same key first, drop ours and report duplicate.
        return store.TryAdd(evt) ? evt : null;
    }

    private static string BuildIdempotencyKey(NotificationTrigger t) =>
        $"{t.Stage}:{t.SourceKey}:{t.OccurredAt.UtcDateTime:O}";

    private static NotificationEvent Draft(NotificationTrigger t, DateTimeOffset now, string key) => new()
    {
        Id = string.Empty,
        IdempotencyKey = key,
        Stage = t.Stage,
        Title = t.Title,
        Summary = t.Summary,
        LoadId = t.LoadId,
        LoadNumber = t.LoadNumber,
        PlanId = t.PlanId,
        LinkPath = t.LinkPath,
        OccurredAt = t.OccurredAt,
        FiredAt = now,
        Deliveries = [],
    };

    private IReadOnlyList<NotificationRecipient> ResolveRecipients(NotificationStage stage)
    {
        if (_options.Recipients.TryGetValue(stage.ToString(), out var configured) && configured.Count > 0)
        {
            return configured
                .Where(r => !string.IsNullOrWhiteSpace(r.Name))
                .Select(r => new NotificationRecipient
                {
                    Name = r.Name.Trim(),
                    Channel = r.Channel,
                    Address = string.IsNullOrWhiteSpace(r.Address) ? null : r.Address.Trim(),
                })
                .ToArray();
        }

        return DefaultRecipients(stage);
    }

    /// <summary>
    /// Built-in default audiences per the owner spec. All in-app role labels (no external address),
    /// so a fresh deployment shows an honest, useful feed before any recipient config is added.
    /// </summary>
    private static IReadOnlyList<NotificationRecipient> DefaultRecipients(NotificationStage stage)
    {
        var names = stage switch
        {
            NotificationStage.ConsolidationPlanCreated => new[] { "dispatcher", "load planner" },
            NotificationStage.ClickCardGenerated => ["dispatcher", "ops lead", "account owner"],
            NotificationStage.AssignmentConfirmed => ["dispatcher", "driver manager"],
            NotificationStage.PickupEvent => ["dispatcher", "customer rep"],
            NotificationStage.DeliveryEvent => ["dispatcher", "billing", "customer rep"],
            NotificationStage.BillingReady => ["billing team"],
            NotificationStage.Invoiced => ["billing", "account owner"],
            NotificationStage.ExceptionRaised => ["dispatcher", "ops lead"],
            NotificationStage.OpportunityDetected => ["dispatcher", "load planner", "account owner"],
            NotificationStage.ArDigest => ["billing", "account owner"],
            _ => new[] { "dispatcher" },
        };

        return names
            .Select(n => new NotificationRecipient { Name = n, Channel = NotificationChannelKind.InApp })
            .ToArray();
    }
}
