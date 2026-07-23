using LtlTool.Api.Features.Integrations.Yard;
using LtlTool.Api.Features.Integrations.Yard.Webhooks;
using LtlTool.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Yard;

/// <summary>
/// Inbound Yard webhook receiver plus a read-only admin listing for the ops panel.
///
/// <para>
/// The whole controller is gated behind <c>Yard:Webhooks:Enabled</c>: when the receiver is disabled it
/// returns 404 (the boundary is dormant). The receiver (<see cref="Receive"/>) is deliberately
/// <b>anonymous</b> — the Yard is a machine caller with no email identity — but not unauthenticated: every
/// request must carry a valid HMAC-SHA256 signature over the timestamp + raw body, and stale timestamps
/// are rejected. Without a signing secret it fails closed (503). It verifies, persists the raw event, and
/// acks fast (200); processing (cache invalidation / opportunity persistence / SignalR fan-out) happens
/// off-thread.
/// </para>
///
/// <para>
/// The admin listing (<see cref="Recent"/>) stays behind the normal <c>AllowedEmailDomain</c> policy.
/// </para>
/// </summary>
[ApiController]
[Route("api/yard/webhooks")]
[Produces("application/json")]
public sealed class YardWebhookController(
    IYardWebhookSignatureVerifier verifier,
    IYardWebhookStore store,
    IYardWebhookProcessingQueue queue,
    IOptions<YardWebhookOptions> webhookOptions,
    TimeProvider clock,
    ILogger<YardWebhookController> logger) : ControllerBase
{
    public const string EventHeader = "X-Yard-Event";
    public const string EventIdHeader = "X-Yard-Event-Id";
    public const string TimestampHeader = "X-Yard-Timestamp";
    public const string SignatureHeader = "X-Yard-Signature";

    /// <summary>Cap on the raw body we persist; a webhook snapshot is far smaller than this.</summary>
    private const int MaxRawBodyBytes = 1_048_576;

    private const int DefaultRecentLimit = 50;
    private const int MaxRecentLimit = 200;

    /// <summary>
    /// Receives one Yard webhook delivery. Returns 404 when the receiver is disabled. Verifies the
    /// signature, dedupes on the event id, persists the raw event, and acks immediately. Duplicate
    /// deliveries (same event id) are acked with 200 without reprocessing. Signature/timestamp failures
    /// return 401; a missing signing secret returns 503.
    /// </summary>
    [HttpPost("receiver")]
    [AllowAnonymous]
    [RequestSizeLimit(MaxRawBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        if (!webhookOptions.Value.Enabled)
            return NotFound();

        string rawBody;
        using (var reader = new StreamReader(Request.Body, System.Text.Encoding.UTF8))
            rawBody = await reader.ReadToEndAsync(ct);

        var now = clock.GetUtcNow();
        var signature = Request.Headers[SignatureHeader].ToString();
        var verify = verifier.Verify(signature, rawBody, now);

        switch (verify)
        {
            case YardWebhookVerifyResult.NotConfigured:
                // Fail-closed: without a signing secret we cannot trust any request. Operator fix, not a
                // client error.
                logger.LogError(
                    "Yard webhook rejected: no signing secret configured (Yard:Webhooks:Secret). Fail-closed.");
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            case YardWebhookVerifyResult.MalformedSignature:
            case YardWebhookVerifyResult.StaleTimestamp:
            case YardWebhookVerifyResult.SignatureMismatch:
                // Do not log the signature or body — audit the failure reason only.
                logger.LogWarning("Yard webhook signature rejected: {Reason}.", verify);
                return Unauthorized();
        }

        var eventId = Request.Headers[EventIdHeader].ToString();
        if (string.IsNullOrWhiteSpace(eventId))
            return BadRequest($"Missing {EventIdHeader} header.");

        var envelope = YardWebhookPayload.TryParse(rawBody);
        var eventType = Request.Headers[EventHeader].ToString();
        if (string.IsNullOrWhiteSpace(eventType))
            eventType = envelope?.EventType ?? "unknown";
        long.TryParse(Request.Headers[TimestampHeader].ToString(), out var timestamp);

        var evt = new YardWebhookEvent
        {
            EventId = eventId,
            EventType = eventType,
            Timestamp = timestamp,
            YardCode = envelope?.YardCode,
            TractorId = envelope?.TractorId,
            TrailerId = envelope?.TrailerId,
            DriverId = envelope?.DriverId,
            RawBody = rawBody,
            ReceivedAt = now,
        };

        var inserted = store.TryInsertReceived(evt);
        if (!inserted)
        {
            // At-least-once delivery made idempotent: a duplicate is acked without reprocessing.
            logger.LogInformation("Yard webhook duplicate delivery acked (event {EventId}).", eventId);
            return Ok(new { status = "duplicate", eventId });
        }

        // Hand off to the background processor and ack immediately — processing never blocks the ack.
        await queue.EnqueueAsync(eventId, ct);
        return Ok(new { status = "received", eventId });
    }

    /// <summary>Recent received events (newest first) plus receiver configuration, for the ops panel.</summary>
    [HttpGet("events")]
    [Authorize(Policy = AccessPolicies.AllowedEmailDomain)]
    [ProducesResponseType(typeof(YardWebhookAdminView), StatusCodes.Status200OK)]
    public ActionResult<YardWebhookAdminView> Recent([FromQuery] int? max)
    {
        var limit = Math.Clamp(max ?? DefaultRecentLimit, 1, MaxRecentLimit);
        var opts = webhookOptions.Value;
        var events = store.ListRecent(limit).Select(YardWebhookEventView.From).ToArray();
        return Ok(new YardWebhookAdminView(
            events,
            store.Count(),
            opts.Enabled,
            opts.HasSecret,
            opts.ToleranceSeconds));
    }
}
