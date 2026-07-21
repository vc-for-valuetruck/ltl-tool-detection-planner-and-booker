using System.Security.Claims;
using LtlTool.Api.Features.Ltl.Arrivals;
using LtlTool.Api.Features.Ltl.Assignment;
using LtlTool.Api.Features.Ltl.Consolidation;
using LtlTool.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LtlTool.Api.Features.Ltl;

/// <summary>
/// LTL decision-support API: the normalized search grid, single-load detail, explainable
/// driver/equipment match recommendations, billing readiness/worklist, the exception list and
/// the internal (audited, non-Alvys) assignment-decision boundary.
///
/// <para>
/// Everything here is read-only against Alvys. The one mutating endpoint —
/// <see cref="Assign"/> — records the decision in a local audit store and deliberately does
/// <b>not</b> push trip/driver assignment upstream (the Alvys integration is read-only in this
/// phase). The response makes that boundary explicit via
/// <see cref="AssignmentAudit.AlvysWriteback"/>.
/// </para>
/// </summary>
[ApiController]
[Route("api/ltl")]
[Authorize(Policy = AccessPolicies.AllowedEmailDomain)]
[Produces("application/json")]
public sealed class LtlController(
    LtlLoadService loads, MatchService matches, AssignmentValidationService validation,
    IAssignmentAuditStore auditStore,
    ConsolidationOpportunityService consolidationOpportunityService,
    CapacitySnapshotService capacity,
    LaredoArrivalsService arrivals,
    ILogger<LtlController> logger) : ControllerBase
{
    /// <summary>Normalized, filtered, sorted, paged LTL search over the swept Alvys loads.</summary>
    [HttpGet("search")]
    [ProducesResponseType(typeof(LtlSearchResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<LtlSearchResponse>> Search(
        [FromQuery] LtlSearchQuery query, CancellationToken ct)
        => Ok(await loads.SearchAsync(query, ct));

    [HttpGet("consolidation/opportunities")]
    [ProducesResponseType(typeof(ConsolidationOpportunitiesResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOpportunities(
        [FromQuery] int limit = 10,
        [FromQuery] int lookbackDays = 14,
        [FromQuery] bool debug = false,
        CancellationToken ct = default)
    {
        try
        {
            var opportunities = await consolidationOpportunityService
                .FindOpportunitiesAsync(limit, lookbackDays, ct);
            return Ok(opportunities);
        }
        catch (Exception ex) when (debug)
        {
            return StatusCode(500, new
            {
                error = ex.GetType().FullName,
                message = ex.Message,
                stack = ex.StackTrace,
                inner = ex.InnerException?.Message
            });
        }
    }

    [HttpPost("consolidation/audit")]
    [ProducesResponseType(typeof(ConsolidationAuditResponse), StatusCodes.Status200OK)]
    public IActionResult RecordAudit([FromBody] ConsolidationAuditRequest req)
    {
        var record = new ConsolidationAuditResponse
        {
            AuditId = $"plan-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString()[..4]}",
            RecordedAt = DateTimeOffset.UtcNow,
            RecordedBy = "demo@valuetruck.com",
            ParentLoadNumber = req.ParentLoadNumber,
            SiblingLoadNumbers = req.SiblingLoadNumbers,
            CombinedRevenue = req.CombinedRevenue,
            CombinedRpm = req.CombinedRpm,
        };
        logger.LogInformation(
            "Consolidation audit recorded: {AuditId} for {Parent}",
            record.AuditId,
            req.ParentLoadNumber);
        return Ok(record);
    }

    /// <summary>
    /// Single-load detail by id/loadNumber/orderNumber, normalized with POD-aware billing.
    /// Returns 404 when Alvys has no such load (or degraded to null).
    /// </summary>
    [HttpGet("loads/{idOrNumber}")]
    [ProducesResponseType(typeof(LtlLoadSummary), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LtlLoadSummary>> GetLoad(string idOrNumber, CancellationToken ct)
    {
        var load = await loads.GetDetailAsync(idOrNumber, ct);
        return load is null ? NotFound() : Ok(load);
    }

    /// <summary>
    /// Ranked, explainable driver/equipment match recommendations for a load. Returns 404 when
    /// the load is not found. <paramref name="top"/> caps the number of recommendations.
    /// </summary>
    [HttpGet("loads/{idOrNumber}/matches")]
    [ProducesResponseType(typeof(IReadOnlyList<MatchResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<MatchResult>>> GetMatches(
        string idOrNumber, [FromQuery] int top, CancellationToken ct)
    {
        var load = await loads.GetDetailAsync(idOrNumber, ct);
        if (load is null) return NotFound();

        return Ok(await matches.RecommendAsync(load, top, ct));
    }

    /// <summary>Billing-readiness evaluation for a single load (POD-aware). 404 when not found.</summary>
    [HttpGet("loads/{idOrNumber}/billing-readiness")]
    [ProducesResponseType(typeof(BillingReadinessResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BillingReadinessResult>> GetBillingReadiness(
        string idOrNumber, CancellationToken ct)
    {
        var load = await loads.GetDetailAsync(idOrNumber, ct);
        return load is null ? NotFound() : Ok(load.Billing);
    }

    /// <summary>
    /// Accessorial-signal review for a single load: keyword-extracted (and optionally
    /// AI-supplemented) evidence of unbilled accessorials from Alvys notes and document
    /// metadata. Returns <see cref="AccessorialReviewContext.NotEvaluated"/> when the load has
    /// no notes or documents (not evaluated ≠ clean). 404 when the load is not found.
    ///
    /// <para>Read-only posture: no data is written back to Alvys.</para>
    /// <para>AI is a signal-extraction layer only — it never prices or asserts dollar amounts.</para>
    /// </summary>
    [HttpGet("loads/{idOrNumber}/accessorial-signals")]
    [ProducesResponseType(typeof(AccessorialReviewContext), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AccessorialReviewContext>> GetAccessorialSignals(
        string idOrNumber, CancellationToken ct)
    {
        var context = await loads.GetAccessorialSignalsAsync(idOrNumber, ct);
        return context is null ? NotFound() : Ok(context);
    }

    /// <summary>
    /// Validates a proposed internal assignment without recording it — the UI calls this to show
    /// blockers/warnings before the dispatcher commits. Returns 404 when the load is not found.
    /// </summary>
    [HttpPost("loads/{idOrNumber}/assign/validate")]
    [ProducesResponseType(typeof(AssignmentValidationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AssignmentValidationResult>> ValidateAssignment(
        string idOrNumber, [FromBody] AssignmentRequest request, CancellationToken ct)
    {
        var (_, result) = await BuildValidationAsync(idOrNumber, request, ct);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>
    /// Records an internal assignment decision for a load in the local audit store, after
    /// validating it against the load and the resolved fleet candidate. Hard rule violations
    /// (no/terminated driver, expired credentials, over-capacity) return
    /// <c>422 Unprocessable Entity</c> and record nothing; non-blocking warnings are allowed
    /// through and captured on the audit. This does <b>not</b> write back to Alvys — the response
    /// carries <see cref="AssignmentAudit.AlvysWriteback"/> = <c>"NotPerformed"</c> to make that
    /// boundary explicit. Returns 404 when the load cannot be resolved upstream.
    /// </summary>
    [HttpPost("loads/{idOrNumber}/assign")]
    [ProducesResponseType(typeof(AssignmentAudit), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(AssignmentValidationResult), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AssignmentAudit>> Assign(
        string idOrNumber, [FromBody] AssignmentRequest request, CancellationToken ct)
    {
        var (load, result) = await BuildValidationAsync(idOrNumber, request, ct);
        if (load is null || result is null) return NotFound();

        if (result.HasBlockers)
            return UnprocessableEntity(result);

        var loadId = load.Id is { Length: > 0 } ? load.Id : idOrNumber;
        var audit = auditStore.Record(loadId, request, CurrentUser(), result.Warnings.ToList());
        return CreatedAtAction(nameof(GetAssignments), new { idOrNumber = loadId }, audit);
    }

    /// <summary>
    /// Resolves and normalizes the load, resolves the fleet candidate by the request ids and runs
    /// assignment validation. Returns <c>(null, null)</c> when the load is not found.
    /// </summary>
    private async Task<(LtlLoadSummary? Load, AssignmentValidationResult? Result)> BuildValidationAsync(
        string idOrNumber, AssignmentRequest request, CancellationToken ct)
    {
        var load = await loads.GetDetailAsync(idOrNumber, ct);
        if (load is null) return (null, null);

        var candidate = await matches.ResolveCandidateAsync(
            request.DriverId, request.TruckId, request.TrailerId, ct);

        // Fold equipment events (repair/maintenance overlapping the load window) into validation as
        // a non-blocking warning, batch-fetched for just this candidate.
        var events = await matches.FetchEquipmentEventsAsync(load, [candidate], ct);
        var assessment = matches.AssessCandidate(load, candidate, events);

        return (load, validation.Validate(load, request, candidate, assessment));
    }

    /// <summary>The recorded internal assignment-decision history for a load (audit trail).</summary>
    [HttpGet("loads/{idOrNumber}/assignments")]
    [ProducesResponseType(typeof(IReadOnlyList<AssignmentAudit>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<AssignmentAudit>> GetAssignments(string idOrNumber)
        => Ok(auditStore.ForLoad(idOrNumber));

    /// <summary>
    /// Billing worklist: loads still needing billing attention, optionally filtered to a single
    /// badge, sorted readiness-first.
    /// </summary>
    [HttpGet("billing/worklist")]
    [ProducesResponseType(typeof(IReadOnlyList<LtlLoadSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<LtlLoadSummary>>> BillingWorklist(
        [FromQuery] BillingBadge? badge, CancellationToken ct)
        => Ok(await loads.BillingWorklistAsync(badge, ct));

    /// <summary>Loads carrying one or more operational/billing exceptions.</summary>
    [HttpGet("exceptions")]
    [ProducesResponseType(typeof(IReadOnlyList<LtlLoadSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<LtlLoadSummary>>> Exceptions(CancellationToken ct)
        => Ok(await loads.ExceptionsAsync(ct));

    /// <summary>
    /// Read-only margin/exception rollup grouped by customer, rep, or lane — profitability
    /// visibility over the same normalized load set the billing worklist uses. No Alvys
    /// writeback, and no external system is ingested here — see <see cref="MarginRollupExport"/>
    /// for the CSV shape of this same data, for a BI tool to read as output.
    /// </summary>
    [HttpGet("reporting/margin-rollup")]
    [ProducesResponseType(typeof(MarginRollupResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MarginRollupResponse>> MarginRollup(
        [FromQuery] RollupGroupBy groupBy = RollupGroupBy.Customer, CancellationToken ct = default)
        => Ok(await loads.GetMarginRollupAsync(groupBy, ct));

    /// <summary>
    /// CSV export of the margin rollup for external reporting tools (e.g. Power BI's Text/CSV
    /// connector). Same authorization and the same read-only Alvys-derived data as
    /// <see cref="MarginRollup"/> — only the response shape changes; nothing new is ingested and
    /// nothing is written back to Alvys.
    /// </summary>
    [HttpGet("reporting/margin-rollup/export")]
    [Produces("text/csv")]
    public async Task<IActionResult> MarginRollupExport(
        [FromQuery] RollupGroupBy groupBy = RollupGroupBy.Customer, CancellationToken ct = default)
    {
        var result = await loads.GetMarginRollupAsync(groupBy, ct);
        var csv = MarginRollupCsvWriter.Write(result);
        var bytes = System.Text.Encoding.UTF8.GetBytes(csv);
        return File(bytes, "text/csv", $"margin-rollup-{groupBy}.csv".ToLowerInvariant());
    }

    /// <summary>
    /// Live "Capacity today" snapshot (Phase 7.4): active trucks, trailer pool by equipment type,
    /// and in-transit trips — every count a read-only Alvys sweep, truncation reported honestly.
    /// </summary>
    [HttpGet("capacity/today")]
    [ProducesResponseType(typeof(CapacitySnapshot), StatusCodes.Status200OK)]
    public async Task<ActionResult<CapacitySnapshot>> CapacityToday(CancellationToken ct)
        => Ok(await capacity.GetSnapshotAsync(ct));

    /// <summary>
    /// Laredo Arrivals Board (Phase 8.1): every truck/trailer scheduled to arrive at the Laredo
    /// yard on the given day (default: today, UTC), Dallas-bound first. Every value is a read-only
    /// Alvys trip/stop read; a capped sweep is reported via <see cref="LaredoArrivalsBoard.Truncated"/>.
    /// </summary>
    [HttpGet("arrivals")]
    [ProducesResponseType(typeof(LaredoArrivalsBoard), StatusCodes.Status200OK)]
    public async Task<ActionResult<LaredoArrivalsBoard>> Arrivals(
        [FromQuery] DateOnly? date, CancellationToken ct)
        => Ok(await arrivals.GetBoardAsync(date, ct));

    /// <summary>
    /// Recent lane rate context (Phase 7.4): the revenue-per-mile spread across recently delivered
    /// loads on the same origin→destination state pair. Recent tenant history, not a market rate.
    /// </summary>
    [HttpGet("lane-rate")]
    [ProducesResponseType(typeof(LaneRateContext), StatusCodes.Status200OK)]
    public async Task<ActionResult<LaneRateContext>> LaneRate(
        [FromQuery] string originState, [FromQuery] string destinationState, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(originState) || string.IsNullOrWhiteSpace(destinationState))
            return BadRequest("originState and destinationState are both required.");

        return Ok(await loads.LaneRateContextAsync(originState, destinationState, ct));
    }

    private string CurrentUser() =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.Email)
        ?? User.Identity?.Name
        ?? "unknown";
}
