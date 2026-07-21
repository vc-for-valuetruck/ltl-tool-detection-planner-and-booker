using System.Security.Claims;
using LtlTool.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.Consolidation;

/// <summary>
/// Consolidation planner API for the Phase 1 Laredo → Dallas pilot.
/// Read-only against Alvys — every response is derived from live loads and static config;
/// nothing writes upstream. See ROADMAP § 2.5 and docs/PILOT_LAREDO_DALLAS.md for scope.
/// </summary>
[ApiController]
[Route("api/ltl/consolidation")]
[Authorize(Policy = AccessPolicies.AllowedEmailDomain)]
[Produces("application/json")]
public sealed class ConsolidationController(
    ConsolidationCandidateService candidates,
    ConsolidationPlanService plans,
    IConsolidationAuditStore audits,
    ILaneTemplateStore laneTemplates,
    IOptions<ConsolidationOptions> options,
    CorridorHealthCache corridorHealth,
    ILogger<ConsolidationController> logger) : ControllerBase
{
    private readonly ConsolidationOptions _options = options.Value;
    private readonly CorridorHealthCache _corridorHealth = corridorHealth;
    private readonly ILaneTemplateStore _laneTemplates = laneTemplates;
    private readonly ILogger<ConsolidationController> _logger = logger;

    /// <summary>
    /// Lists the consolidation corridors the planner recognises today, joined with each
    /// corridor's origin and destination warehouse metadata. Read-only, honest projection of
    /// static config — not a place to declare arbitrary new corridors at runtime. Any UI or
    /// automation that needs to know "where can I plan?" reads from here.
    ///
    /// <para>Used by the demo-workflow Playwright suite to pick a corridor with live loads;
    /// hardcoding LAREDO_TO_DALLAS in that suite would ossify the pilot's shape into E2E.</para>
    /// </summary>
    [HttpGet("corridors")]
    [ProducesResponseType(typeof(IReadOnlyList<CorridorSummary>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<CorridorSummary>> GetCorridors()
    {
        // Build a warehouse-code -> warehouse lookup once so per-corridor projection is O(1).
        var warehouseByCode = _options.Warehouses.ToDictionary(
            w => w.Code, w => w, StringComparer.OrdinalIgnoreCase);

        var summaries = _options.Corridors
            .Where(c => warehouseByCode.ContainsKey(c.OriginWarehouseCode)
                     && warehouseByCode.ContainsKey(c.DestinationWarehouseCode))
            .Select(c =>
            {
                var origin = warehouseByCode[c.OriginWarehouseCode];
                var destination = warehouseByCode[c.DestinationWarehouseCode];
                return new CorridorSummary
                {
                    Code = c.Code,
                    Origin = new WarehouseSummary
                    {
                        Code = origin.Code,
                        Name = origin.Name,
                        State = origin.State,
                        NearbyCities = origin.NearbyCities,
                    },
                    Destination = new WarehouseSummary
                    {
                        Code = destination.Code,
                        Name = destination.Name,
                        State = destination.State,
                        NearbyCities = destination.NearbyCities,
                    },
                    PickupWindowDays = c.PickupWindowDays,
                    DeliveryWindowDays = c.DeliveryWindowDays,
                };
            })
            .ToList();

        return Ok(summaries);
    }

    /// <summary>
    /// Live health snapshot for every configured corridor: how many open loads sit on the
    /// canonical origin → destination lane right now. Meant for the UI corridor picker so
    /// leadership can see at a glance which corridors have plannable pairs before typing an
    /// anchor load id.
    ///
    /// <para>
    /// <b>Served from a cache, never inline.</b> The underlying sweep (a bounded two-sided
    /// nearby-cities cross-product of tiny PageSize=1 Alvys reads — see
    /// <see cref="CorridorHealthProbe"/>) is expensive enough that running it on the request path
    /// hung this endpoint for 10s+. Instead <see cref="CorridorHealthCache"/> serves the last
    /// computed snapshot instantly and refreshes it in the background (stale-while-revalidate with
    /// a hard timeout). A cold cache returns an empty list and kicks off the first refresh — the UI
    /// renders corridor chips from <c>/corridors</c> immediately and treats health as a progressive
    /// enhancement. The payload's <c>asOf</c> field carries the snapshot's timestamp (<c>null</c> on
    /// a cold cache) so the UI can show an honest "as of" stamp; the <c>X-Corridor-Health-As-Of</c>
    /// header mirrors it for non-UI callers. The full candidate walk still happens when the
    /// dispatcher picks a corridor and enters an anchor load.
    /// </para>
    /// </summary>
    [HttpGet("corridors/health")]
    [ProducesResponseType(typeof(CorridorHealthSnapshotResponse), StatusCodes.Status200OK)]
    public ActionResult<CorridorHealthSnapshotResponse> GetCorridorHealth()
    {
        var snapshot = _corridorHealth.GetSnapshot();
        if (snapshot is not null)
        {
            Response.Headers["X-Corridor-Health-As-Of"] = snapshot.AsOf.ToString("O");
            return Ok(new CorridorHealthSnapshotResponse { AsOf = snapshot.AsOf, Corridors = snapshot.Healths });
        }

        // Cold cache: return an honest empty snapshot now (the refresh is already in flight) rather
        // than blocking the caller on the sweep. The picker stays populated from /corridors.
        return Ok(new CorridorHealthSnapshotResponse { AsOf = null, Corridors = Array.Empty<CorridorHealth>() });
    }

    /// <summary>
    /// Returns ranked consolidation candidates for the given seed load along the specified
    /// corridor. Missing seed → 200 with a null Seed and empty candidate list.
    /// Unknown corridor → 400.
    /// </summary>
    [HttpGet("candidates")]
    [ProducesResponseType(typeof(ConsolidationCandidateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ConsolidationCandidateResponse>> GetCandidates(
        [FromQuery] string loadId,
        [FromQuery] string? corridor,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(loadId))
        {
            return BadRequest(new { error = "loadId query parameter is required." });
        }

        var corridorCode = string.IsNullOrWhiteSpace(corridor) ? "LAREDO_TO_DALLAS" : corridor;

        try
        {
            var response = await candidates.GetCandidatesAsync(loadId, corridorCode, ct);
            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Builds a plan preview: resolves parent + siblings against Alvys, applies corridor and
    /// customer-policy gates, and returns the projected combined-RPM plus the copy-pasteable
    /// Alvys click card. 400 on unknown corridor / missing parent / missing siblings; 200 with
    /// non-empty <see cref="ConsolidationPlanResponse.Blockers"/> when the plan resolves but
    /// is illegal (Never-consolidate customer, corridor mismatch, etc.).
    /// </summary>
    [HttpPost("plan")]
    [ProducesResponseType(typeof(ConsolidationPlanResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ConsolidationPlanResponse>> BuildPlan(
        [FromBody] ConsolidationPlanRequest request,
        CancellationToken ct)
    {
        try
        {
            var response = await plans.BuildAsync(request, ct);

            // Effectiveness metrics (Phase 4): status-only, no PII. Lets leadership see how many
            // plans the tool generates, how many carried blockers / red-RPM warnings / accessorial
            // pre-checks, and (via plan/audit) how many were acted on — the adoption denominator.
            _logger.LogInformation(
                "Consolidation metric: plan_generated corridor={Corridor} siblings={SiblingCount} "
                + "blockers={BlockerCount} rpmWarning={RpmWarningStatus} accessorialPreChecks={PreCheckCount} "
                + "likelyAccessorials={LikelyAccessorialCount}",
                response.CorridorCode,
                response.Siblings.Count,
                response.Blockers.Count,
                response.RpmWarning?.Status.ToString() ?? "None",
                response.AccessorialPreChecks.Count,
                response.AccessorialPreChecks.Sum(p => p.Candidates.Count(
                    c => c.Status == AccessorialCandidateStatus.Likely)));

            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Records a plan preview as an internal audit entry. The dispatcher hits this once
    /// they've decided to execute the click card manually in Alvys. The audit trail is the
    /// leadership-facing counter-signal to commission politics (anti-failure map 3h) — the
    /// running record of consolidation value the tool caught. Nothing writes to Alvys.
    /// </summary>
    [HttpPost("plan/audit")]
    [ProducesResponseType(typeof(ConsolidationAuditRecord), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ConsolidationAuditRecord>> RecordPlanAudit(
        [FromBody] ConsolidationPlanRequest request,
        CancellationToken ct)
    {
        try
        {
            var plan = await plans.BuildAsync(request, ct);
            var record = audits.Record(plan, CurrentUser());

            // Effectiveness metric (Phase 4): a plan that was acted on (recorded to the audit
            // trail). Status-only, no PII — the recording user is not logged here.
            _logger.LogInformation(
                "Consolidation metric: plan_audited corridor={Corridor} siblings={SiblingCount} "
                + "hadBlockers={HadBlockers} combinedRpmKnown={CombinedRpmKnown}",
                record.CorridorCode,
                record.SiblingLoadIds.Count,
                record.Blockers.Count > 0,
                record.CombinedRevenuePerMile is not null);

            return Ok(record);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Audit history for one parent load, newest first.</summary>
    [HttpGet("plan/audits")]
    [ProducesResponseType(typeof(IReadOnlyList<ConsolidationAuditRecord>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<ConsolidationAuditRecord>> GetPlanAuditsForParent(
        [FromQuery] string parentLoadId)
    {
        if (string.IsNullOrWhiteSpace(parentLoadId))
        {
            return Ok(audits.All());
        }
        return Ok(audits.ForParent(parentLoadId));
    }

    /// <summary>
    /// Combined-RPM billing view (Phase 4) for a load that was recorded as a consolidation parent.
    /// Billing detail must show the plan's combined revenue and combined driver miles/RPM — not the
    /// parent's inflated stand-alone RPM. Sourced from the most-recent audit for the parent; 200 with
    /// <c>Found=false</c> when the load has no consolidation audit (the SPA then shows the normal
    /// single-load billing view). Read-only; every value is echoed from the audit, never re-derived.
    /// </summary>
    [HttpGet("plan/combined-rpm")]
    [ProducesResponseType(typeof(CombinedPlanBillingView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<CombinedPlanBillingView> GetCombinedRpm([FromQuery] string loadId)
    {
        if (string.IsNullOrWhiteSpace(loadId))
        {
            return BadRequest(new { error = "loadId query parameter is required." });
        }

        var record = audits.ForParent(loadId).FirstOrDefault();
        if (record is null)
        {
            return Ok(new CombinedPlanBillingView { Found = false });
        }

        return Ok(new CombinedPlanBillingView
        {
            Found = true,
            AuditId = record.Id,
            CorridorCode = record.CorridorCode,
            ParentLoadId = record.ParentLoadId,
            ParentLoadNumber = record.ParentLoadNumber,
            SiblingLoadNumbers = record.SiblingLoadNumbers,
            CombinedRevenue = record.CombinedRevenue,
            DriverLoadedMiles = record.DriverLoadedMiles,
            CombinedDriverTripValue = record.CombinedDriverTripValue,
            CombinedRevenuePerMile = record.CombinedRevenuePerMile,
            RecordedAt = record.RecordedAt,
        });
    }

    /// <summary>
    /// Effectiveness metric (Phase 4): the dispatcher copied a plan's click card. Fire-and-forget
    /// status-only signal — no plan body, no PII — so leadership can see how many generated plans
    /// actually reached the "paste into Alvys" step (candidates surfaced vs acted on).
    /// </summary>
    [HttpPost("plan/metrics/click-card-copied")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult RecordClickCardCopied([FromBody] ClickCardCopiedRequest? request)
    {
        _logger.LogInformation(
            "Consolidation metric: click_card_copied corridor={Corridor} siblings={SiblingCount}",
            string.IsNullOrWhiteSpace(request?.CorridorCode) ? "unknown" : request!.CorridorCode,
            request?.SiblingCount ?? 0);
        return NoContent();
    }

    /// <summary>
    /// Saves a recurring-lane template (Phase 2.5): a named note of a corridor/customer/cadence the
    /// dispatcher expects to run again. Internal Value Truck data — never an Alvys write, never a live
    /// load id. 400 on missing name/corridor.
    /// </summary>
    [HttpPost("plan/templates")]
    [ProducesResponseType(typeof(LaneTemplateView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<LaneTemplateView> SaveLaneTemplate([FromBody] SaveLaneTemplateRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "name is required." });
        }
        if (string.IsNullOrWhiteSpace(request.CorridorCode))
        {
            return BadRequest(new { error = "corridorCode is required." });
        }

        var now = DateTimeOffset.UtcNow;
        var record = _laneTemplates.Add(new LaneTemplateRecord
        {
            Id = $"lane-{Guid.NewGuid():n}",
            Name = request.Name.Trim(),
            CorridorCode = request.CorridorCode.Trim(),
            CustomerName = string.IsNullOrWhiteSpace(request.CustomerName) ? null : request.CustomerName.Trim(),
            OriginLabel = string.IsNullOrWhiteSpace(request.OriginLabel) ? null : request.OriginLabel.Trim(),
            DestinationLabel = string.IsNullOrWhiteSpace(request.DestinationLabel) ? null : request.DestinationLabel.Trim(),
            CadenceDays = request.CadenceDays,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            CreatedBy = CurrentUser(),
            CreatedAt = now,
            UpdatedAt = now,
        });

        _logger.LogInformation(
            "Consolidation metric: lane_template_saved corridor={Corridor} hasCustomer={HasCustomer} "
            + "cadenceDays={CadenceDays}",
            record.CorridorCode,
            record.CustomerName is not null,
            record.CadenceDays);

        return Ok(LaneTemplateMapping.ToView(record));
    }

    /// <summary>
    /// Lists recurring-lane templates, newest first, optionally filtered by corridor and/or customer.
    /// The SPA uses this to surface "this lane again this week" candidates. Read-only, internal data.
    /// </summary>
    [HttpGet("plan/templates")]
    [ProducesResponseType(typeof(IReadOnlyList<LaneTemplateView>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<LaneTemplateView>> GetLaneTemplates(
        [FromQuery] string? corridorCode,
        [FromQuery] string? customerName)
    {
        var results = _laneTemplates
            .Query(new LaneTemplateQuery(corridorCode, customerName, _options.MaxCandidatesReturned))
            .Select(LaneTemplateMapping.ToView)
            .ToArray();
        return Ok(results);
    }

    /// <summary>Deletes a recurring-lane template by id. 404 when no row matched.</summary>
    [HttpDelete("plan/templates/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult DeleteLaneTemplate(string id) =>
        _laneTemplates.Delete(id) ? NoContent() : NotFound();

    private string CurrentUser() =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.Email)
        ?? User.Identity?.Name
        ?? "unknown";
}
