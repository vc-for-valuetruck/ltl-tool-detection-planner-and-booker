using System.Security.Claims;
using LtlTool.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LtlTool.Api.Features.Ltl.Signals;

/// <summary>
/// Phase 6 inbound signal API: turn unstructured text (note / email / transcript / call summary)
/// into typed, evidence-backed LTL signals, then let a dispatcher accept or reject each one.
///
/// <para><b>Fail-closed.</b> <see cref="Ingest"/> returns 422 with a legible message and records
/// nothing when the extractor fails or produces a signal without a verbatim evidence quote.</para>
///
/// <para><b>Alvys posture: read-only.</b> Ingestion writes only to the internal signal store.
/// Accepting a signal annotates internal LTL surfaces (Search filter, Billing badge, Exception,
/// Match warning, Saved view, Audit note, Next-best-action) — it never mutates Alvys.</para>
/// </summary>
[ApiController]
[Route("api/ltl/signals")]
[Authorize(Policy = AccessPolicies.AllowedEmailDomain)]
[Produces("application/json")]
public sealed class SignalsController(
    SignalIngestService ingest,
    ISignalStore store,
    ISignalExtractor extractor,
    TimeProvider clock) : ControllerBase
{
    /// <summary>
    /// Extract signals from a piece of text and record them (status = Pending). Fails closed:
    /// a 422 with a legible error and no writes if extraction fails or evidence is missing.
    /// </summary>
    [HttpPost("ingest")]
    [ProducesResponseType(typeof(SignalIngestResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Ingest([FromBody] SignalIngestRequest request, CancellationToken ct)
    {
        if (request is null)
            return UnprocessableEntity(new { error = "A JSON body with text + source metadata is required." });

        try
        {
            var result = await ingest.IngestAsync(request, CurrentUser(), ct);
            return Ok(result);
        }
        catch (SignalIngestException ex)
        {
            // Fail-closed: nothing was persisted. Surface the reason legibly.
            return UnprocessableEntity(new { error = ex.Message });
        }
    }

    /// <summary>Recorded signals, newest first, filterable by status / source type / load number.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<SignalView>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<SignalView>> List(
        [FromQuery] SignalStatus? status,
        [FromQuery] string? sourceType,
        [FromQuery] string? loadNumber,
        [FromQuery] int max = 100)
        => Ok(store.Query(new SignalQuery(status, sourceType, loadNumber, max))
            .Select(SignalMapping.ToView)
            .ToArray());

    /// <summary>Honest snapshot of which extractor is active (deterministic keyword vs. an LLM).</summary>
    [HttpGet("extractor")]
    [ProducesResponseType(typeof(SignalExtractorStatus), StatusCodes.Status200OK)]
    public ActionResult<SignalExtractorStatus> Extractor()
        => Ok(new SignalExtractorStatus(extractor.Name));

    /// <summary>Accept a signal — annotates the suggested internal surface. Never writes to Alvys.</summary>
    [HttpPost("{id}/accept")]
    [ProducesResponseType(typeof(SignalView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<SignalView> Accept(string id) => Decide(id, SignalStatus.Accepted);

    /// <summary>Reject a signal — kept for audit, annotates nothing.</summary>
    [HttpPost("{id}/reject")]
    [ProducesResponseType(typeof(SignalView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<SignalView> Reject(string id) => Decide(id, SignalStatus.Rejected);

    private ActionResult<SignalView> Decide(string id, SignalStatus status)
    {
        var updated = store.UpdateStatus(id, status, CurrentUser(), clock.GetUtcNow());
        return updated is null ? NotFound() : Ok(SignalMapping.ToView(updated));
    }

    private string CurrentUser() =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.Email)
        ?? User.Identity?.Name
        ?? "unknown";
}

/// <summary>Honest active-extractor snapshot (no secrets — a name only).</summary>
public sealed record SignalExtractorStatus(string Name);
