using LtlTool.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LtlTool.Api.Features.Ltl.Notifications;

/// <summary>
/// Read-only feed API for the LTL workflow notification engine (Phase 6). Serves the in-app
/// bell/feed and an honest per-channel configuration snapshot. Nothing here writes to Alvys —
/// the feed is fired by the background <see cref="NotificationTriggerEngine"/> and served from
/// the in-memory <see cref="INotificationStore"/>.
/// </summary>
[ApiController]
[Route("api/ltl/notifications")]
[Authorize(Policy = AccessPolicies.AllowedEmailDomain)]
[Produces("application/json")]
public sealed class NotificationController(
    INotificationStore store,
    IEnumerable<INotificationChannel> channels,
    IMailOutbox mailOutbox) : ControllerBase
{
    private readonly IReadOnlyList<INotificationChannel> _channels = channels.ToArray();

    /// <summary>
    /// Most recent fired notifications, newest first, plus the total fired since startup and the
    /// current per-channel configuration state. <paramref name="max"/> is clamped to [1, 200].
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(NotificationFeedResponse), StatusCodes.Status200OK)]
    public ActionResult<NotificationFeedResponse> GetFeed([FromQuery] int max = 50)
    {
        max = Math.Clamp(max, 1, 200);
        return Ok(new NotificationFeedResponse
        {
            Total = store.Count,
            Items = store.Recent(max),
            Channels = ChannelStatuses(),
        });
    }

    /// <summary>
    /// Per-channel configuration snapshot on its own so a settings/status panel can render "which
    /// channels are live" without pulling the whole feed. Honest: reports what is actually
    /// configured server-side, never a fabricated "connected".
    /// </summary>
    [HttpGet("channels")]
    [ProducesResponseType(typeof(IReadOnlyList<NotificationChannelStatus>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<NotificationChannelStatus>> GetChannels() =>
        Ok(ChannelStatuses());

    private IReadOnlyList<NotificationChannelStatus> ChannelStatuses()
    {
        // Only the email channel tracks a durable last-send result (via the mail outbox). Other
        // channels report configuration state only. Honest: null last-result means "nothing sent yet".
        var lastMail = mailOutbox.MostRecent();
        return _channels
            .Select(c => new NotificationChannelStatus
            {
                Channel = c.Kind,
                Configured = c.IsConfigured,
                LastSendState = c.Kind == NotificationChannelKind.Email ? lastMail?.State.ToString() : null,
                LastSendDetail = c.Kind == NotificationChannelKind.Email ? lastMail?.Detail : null,
                LastSendAt = c.Kind == NotificationChannelKind.Email ? lastMail?.UpdatedAt : null,
            })
            .OrderBy(c => c.Channel)
            .ToArray();
    }
}

/// <summary>Feed payload: fired events (newest first), lifetime count, and channel state.</summary>
public sealed class NotificationFeedResponse
{
    public required int Total { get; init; }
    public required IReadOnlyList<NotificationEvent> Items { get; init; }
    public required IReadOnlyList<NotificationChannelStatus> Channels { get; init; }
}

/// <summary>
/// Honest configuration state for one channel (no secrets). For the email channel it also carries the
/// last send outcome (Delivered/Failed/NotConfigured) + detail + timestamp so ops can see channel
/// health without reading logs. Null last-send fields mean nothing has been sent on this channel yet.
/// </summary>
public sealed class NotificationChannelStatus
{
    public required NotificationChannelKind Channel { get; init; }
    public required bool Configured { get; init; }
    public string? LastSendState { get; init; }
    public string? LastSendDetail { get; init; }
    public DateTimeOffset? LastSendAt { get; init; }
}
