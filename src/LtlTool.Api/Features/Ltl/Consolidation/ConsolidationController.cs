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
    IOptions<ConsolidationOptions> options,
    CorridorHealthCache corridorHealth) : ControllerBase
{
    private readonly ConsolidationOptions _options = options.Value;
    private readonly CorridorHealthCache _corridorHealth = corridorHealth;

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
    /// enhancement. The <c>X-Corridor-Health-As-Of</c> header carries the snapshot's timestamp
    /// (absent on a cold cache) so the UI can show an honest "as of" stamp. The full candidate walk
    /// still happens when the dispatcher picks a corridor and enters an anchor load.
    /// </para>
    /// </summary>
    [HttpGet("corridors/health")]
    [ProducesResponseType(typeof(IReadOnlyList<CorridorHealth>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<CorridorHealth>> GetCorridorHealth()
    {
        var snapshot = _corridorHealth.GetSnapshot();
        if (snapshot is not null)
        {
            Response.Headers["X-Corridor-Health-As-Of"] = snapshot.AsOf.ToString("O");
            return Ok(snapshot.Healths);
        }

        // Cold cache: return an honest empty snapshot now (the refresh is already in flight) rather
        // than blocking the caller on the sweep. The picker stays populated from /corridors.
        return Ok(Array.Empty<CorridorHealth>());
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

    private string CurrentUser() =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.Email)
        ?? User.Identity?.Name
        ?? "unknown";
}
