using LtlTool.Api.Features.Integrations.Alvys.Webhooks;
using LtlTool.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LtlTool.Api.Features.Alvys;

/// <summary>
/// Inbound Alvys webhook receiver plus a read-only admin listing for the ops panel.
///
/// <para>
/// The receiver (<see cref="Receive"/>) is deliberately <b>anonymous</b> — Alvys is a machine caller
/// with no email identity, so it cannot satisfy the <c>AllowedEmailDomain</c> policy that guards the
/// rest of the API. It is <b>not</b> unauthenticated in the security sense: every request must carry a
/// valid HMAC-SHA256 signature over the timestamp + raw body, and stale timestamps are rejected. The
/// receiver verifies, persists the raw event, and acks fast (200); it never blocks the ack on read-model
/// work, because Alvys auto-disables a subscription after a bounded number of consecutive failed events.
/// </para>
///
/// <para>
/// The admin listing (<see cref="Recent"/>) stays behind the normal <c>AllowedEmailDomain</c> policy —
/// only dispatchers see the received-events history and receiver configuration snapshot.
/// </para>
/// </summary>
[ApiController]
[Route("api/alvys/webhooks")]
[Produces("application/json")]
public sealed class AlvysWebhookController(
    IAlvysWebhookSignatureVerifier verifier,
    IAlvysWebhookStore store,
    IAlvysWebhookProcessingQueue queue,
    Microsoft.Extensions.Options.IOptions<AlvysWebhookOptions> webhookOptions,
    TimeProvider clock,
    ILogger<AlvysWebhookController> logger) : ControllerBase
{
    public const string EventHeader = "X-Alvys-Event";
    public const string EventIdHeader = "X-Alvys-Event-Id";
    public const string TimestampHeader = "X-Alvys-Timestamp";
    public const string SignatureHeader = "X-Alvys-Signature";
    public const string AttemptHeader = "X-Alvys-Attempt";

    /// <summary>Cap on the raw body we persist; a webhook snapshot is far smaller than this.</summary>
    private const int MaxRawBodyBytes = 1_048_576;

    private const int DefaultRecentLimit = 50;
    private const int MaxRecentLimit = 200;

    /// <summary>
    /// Receives one Alvys webhook delivery. Verifies the signature, dedupes on the event id, persists the
    /// raw event, and acks immediately. Duplicate deliveries (same event id) are acked with 200 without
    /// reprocessing. Signature/timestamp failures return 401; a missing signing secret returns 503.
    /// </summary>
    [HttpPost("receiver")]
    [AllowAnonymous]
    [RequestSizeLimit(MaxRawBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        string rawBody;
        using (var reader = new StreamReader(Request.Body, System.Text.Encoding.UTF8))
            rawBody = await reader.ReadToEndAsync(ct);

        var now = clock.GetUtcNow();
        var signature = Request.Headers[SignatureHeader].ToString();
        var verify = verifier.Verify(signature, rawBody, now);

        switch (verify)
        {
            case AlvysWebhookVerifyResult.NotConfigured:
                // Fail-closed: without a signing secret we cannot trust any request. This is a server
                // misconfiguration for an operator to fix, not a client error.
                logger.LogError(
                    "Alvys webhook rejected: no signing secret configured (Alvys:Webhooks:Secret). Fail-closed.");
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            case AlvysWebhookVerifyResult.MalformedSignature:
            case AlvysWebhookVerifyResult.StaleTimestamp:
            case AlvysWebhookVerifyResult.SignatureMismatch:
                // Do not log the signature or body — audit the failure reason only.
                logger.LogWarning("Alvys webhook signature rejected: {Reason}.", verify);
                return Unauthorized();
        }

        var eventId = Request.Headers[EventIdHeader].ToString();
        if (string.IsNullOrWhiteSpace(eventId))
            return BadRequest($"Missing {EventIdHeader} header.");

        var eventType = Request.Headers[EventHeader].ToString();
        long.TryParse(Request.Headers[TimestampHeader].ToString(), out var timestamp);
        int? attempt = int.TryParse(Request.Headers[AttemptHeader].ToString(), out var a) ? a : null;

        var evt = new AlvysWebhookEvent
        {
            EventId = eventId,
            EventType = string.IsNullOrWhiteSpace(eventType) ? "unknown" : eventType,
            Timestamp = timestamp,
            Attempt = attempt,
            LoadNumber = AlvysWebhookPayload.TryExtractLoadNumber(rawBody),
            RawBody = rawBody,
            ReceivedAt = now,
        };

        var inserted = store.TryInsertReceived(evt);
        if (!inserted)
        {
            // At-least-once delivery made idempotent: a duplicate is acked without reprocessing.
            logger.LogInformation("Alvys webhook duplicate delivery acked (event {EventId}).", eventId);
            return Ok(new { status = "duplicate", eventId });
        }

        // Hand off to the background processor and ack immediately — processing never blocks the ack.
        await queue.EnqueueAsync(eventId, ct);
        return Ok(new { status = "received", eventId });
    }

    /// <summary>Recent received events (newest first) plus receiver configuration, for the ops panel.</summary>
    [HttpGet("events")]
    [Authorize(Policy = AccessPolicies.AllowedEmailDomain)]
    [ProducesResponseType(typeof(AlvysWebhookAdminView), StatusCodes.Status200OK)]
    public ActionResult<AlvysWebhookAdminView> Recent([FromQuery] int? max)
    {
        var limit = Math.Clamp(max ?? DefaultRecentLimit, 1, MaxRecentLimit);
        var opts = webhookOptions.Value;
        var events = store.ListRecent(limit).Select(AlvysWebhookEventView.From).ToArray();
        return Ok(new AlvysWebhookAdminView(
            events,
            store.Count(),
            opts.HasSecret,
            opts.ToleranceSeconds,
            opts.AutoDisableThreshold));
    }
}
