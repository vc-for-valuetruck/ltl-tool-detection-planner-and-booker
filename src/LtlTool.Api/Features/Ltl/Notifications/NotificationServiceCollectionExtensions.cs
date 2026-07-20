namespace LtlTool.Api.Features.Ltl.Notifications;

/// <summary>
/// DI wiring for the LTL workflow notification engine (Phase 6). Registers the always-on in-app
/// channel plus the config-gated Teams and email adapters, the idempotent in-memory feed store,
/// the dispatcher, and the background trigger poller. All external channels are honest-off until
/// configured server-side, so a fresh clone / CI / the demo only exercise the in-app feed.
///
/// <para>Alvys posture: read-only. The engine reads internal audit stores and never writes to Alvys.</para>
/// </summary>
public static class NotificationServiceCollectionExtensions
{
    public static IServiceCollection AddLtlNotifications(
        this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<NotificationOptions>()
            .Bind(configuration.GetSection(NotificationOptions.SectionName));

        // Idempotent feed store: singleton in-memory, matching the same posture as the
        // consolidation/assignment audit stores; swap for an EF-backed store in production.
        services.AddSingleton<INotificationStore, InMemoryNotificationStore>();

        // Dispatcher is scoped so it composes fresh channels per sweep — this keeps the Teams
        // typed HttpClient on the factory's handler-rotation lifecycle rather than captured by a
        // singleton. The trigger engine resolves it from a per-sweep scope.
        services.AddScoped<NotificationDispatcher>();

        // Always-on in-app channel: no config, genuinely delivered the moment the feed records it.
        services.AddScoped<INotificationChannel, InAppNotificationChannel>();

        // Teams via incoming webhook: honest "not configured" until Notifications:Teams:WebhookUrl
        // is set. Registered with its own HttpClient so the POST has a bounded transport.
        services.AddHttpClient<INotificationChannel, TeamsNotificationChannel>();

        // Email: honest "not configured" until Notifications:Email is enabled with SMTP host +
        // from-address. SMTP transport is a documented follow-up (reports Pending, never a fake send).
        services.AddScoped<INotificationChannel, EmailNotificationChannel>();

        // Background poller that diffs internal source state → fires new events through the dispatcher.
        services.AddHostedService<NotificationTriggerEngine>();

        return services;
    }
}
