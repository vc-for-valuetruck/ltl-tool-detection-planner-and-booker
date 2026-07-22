using System.Security.Claims;
using LtlTool.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LtlTool.Api.Features.Ltl.Bol;

/// <summary>
/// BOL intelligence API: read a load's Bill of Lading document into <b>suggested</b> fields, then let
/// a human accept or reject each one.
///
/// <para><b>Fail-closed.</b> <see cref="Read"/> returns 422 with a legible message and records nothing
/// when the document can't be fetched, no text can be extracted, or a suggested field lacks a verbatim
/// evidence quote.</para>
///
/// <para><b>Alvys posture: read-only.</b> The read path only downloads a document via the existing
/// #141 document surface. Suggestions are NEVER auto-applied and NEVER written to Alvys. Accepting a
/// field annotates internal surfaces only and is fully audited (who accepted, when, from which
/// document).</para>
/// </summary>
[ApiController]
[Route("api/ltl")]
[Authorize(Policy = AccessPolicies.AllowedEmailDomain)]
[Produces("application/json")]
public sealed class BolController(
    BolReadService reader,
    IBolSuggestionStore store,
    IBolFieldExtractor fieldExtractor,
    IPdfTextExtractor textExtractor,
    TimeProvider clock) : ControllerBase
{
    /// <summary>
    /// Read a load's BOL document into suggested fields (status = Pending). Fails closed: a 422 with a
    /// legible error and no writes if the document can't be fetched/read or evidence is missing.
    /// </summary>
    [HttpPost("loads/{loadNumber}/bol/documents/{documentId}/read")]
    [ProducesResponseType(typeof(BolReadResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Read(string loadNumber, string documentId, CancellationToken ct)
    {
        try
        {
            var result = await reader.ReadAsync(loadNumber, documentId, CurrentUser(), ct);
            return Ok(result);
        }
        catch (BolReadException ex)
        {
            // Fail-closed: nothing was persisted. Surface the reason legibly.
            return UnprocessableEntity(new { error = ex.Message });
        }
    }

    /// <summary>Suggested BOL fields, newest first, filterable by load number and status.</summary>
    [HttpGet("bol/suggestions")]
    [ProducesResponseType(typeof(IReadOnlyList<BolFieldSuggestionView>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<BolFieldSuggestionView>> List(
        [FromQuery] string? loadNumber,
        [FromQuery] BolSuggestionStatus? status,
        [FromQuery] int max = 200)
        => Ok(store.Query(new BolSuggestionQuery(loadNumber, status, max))
            .Select(BolMapping.ToView)
            .ToArray());

    /// <summary>Honest snapshot of which extractors are active (text layer vs. OCR; regex vs. LLM).</summary>
    [HttpGet("bol/extractor")]
    [ProducesResponseType(typeof(BolExtractorStatus), StatusCodes.Status200OK)]
    public ActionResult<BolExtractorStatus> Extractor()
        => Ok(new BolExtractorStatus(textExtractor.Name, fieldExtractor.Name));

    /// <summary>
    /// Accept a suggested field — annotates internal surfaces only, fully audited (who/when/document).
    /// NEVER writes to Alvys.
    /// </summary>
    [HttpPost("bol/suggestions/{id}/accept")]
    [ProducesResponseType(typeof(BolFieldSuggestionView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<BolFieldSuggestionView> Accept(string id) => Decide(id, BolSuggestionStatus.Accepted);

    /// <summary>Reject a suggested field — kept for audit, annotates nothing.</summary>
    [HttpPost("bol/suggestions/{id}/reject")]
    [ProducesResponseType(typeof(BolFieldSuggestionView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<BolFieldSuggestionView> Reject(string id) => Decide(id, BolSuggestionStatus.Rejected);

    private ActionResult<BolFieldSuggestionView> Decide(string id, BolSuggestionStatus status)
    {
        var updated = store.UpdateStatus(id, status, CurrentUser(), clock.GetUtcNow());
        return updated is null ? NotFound() : Ok(BolMapping.ToView(updated));
    }

    private string CurrentUser() =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.Email)
        ?? User.Identity?.Name
        ?? "unknown";
}

/// <summary>Honest active-extractor snapshot (no secrets — names only).</summary>
public sealed record BolExtractorStatus(string TextExtractor, string FieldExtractor);
