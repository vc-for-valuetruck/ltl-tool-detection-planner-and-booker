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
    LtlLoadService loads) : ControllerBase
{
    private readonly ConsolidationOptions _options = options.Value;
    private readonly LtlLoadService _loads = loads;

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
    /// leadership can see at a glance which corridors have plannable pairs before typing a
    /// seed id.
    ///
    /// <para>
    /// <b>Under-counts by design.</b> This runs ONE LTL search per corridor using the first
    /// <see cref="WarehouseSummary.NearbyCities"/> entry (the canonical yard name) on either
    /// side. Loads with origin/destination cities elsewhere in the NearbyCities list won't be
    /// counted here even though they'd be legal siblings. Over-fetching (running the full
    /// nearby-cities cross-product per corridor) would balloon Alvys reads on every UI page-
    /// load. The under-count is honest — the corridor picker doesn't promise "exact"; it
    /// promises "corridor is alive today". The full candidate walk still happens when the
    /// dispatcher picks a corridor and enters a seed.
    /// </para>
    /// </summary>
    [HttpGet("corridors/health")]
    [ProducesResponseType(typeof(IReadOnlyList<CorridorHealth>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CorridorHealth>>> GetCorridorHealth(
        CancellationToken ct)
    {
        var warehouseByCode = _options.Warehouses.ToDictionary(
            w => w.Code, w => w, StringComparer.OrdinalIgnoreCase);

        var healths = new List<CorridorHealth>();
        foreach (var corridor in _options.Corridors)
        {
            if (!warehouseByCode.TryGetValue(corridor.OriginWarehouseCode, out var origin) ||
                !warehouseByCode.TryGetValue(corridor.DestinationWarehouseCode, out var destination))
            {
                // Silently drop misconfigured corridors here — the /corridors listing already
                // filtered them out. This branch is defensive.
                continue;
            }

            // Canonical city = first entry in NearbyCities, by convention the yard's own name.
            // Warehouses ship at least one nearby city so First() is safe on real config, but
            // guard against test/edge configs anyway.
            var originCity = origin.NearbyCities.FirstOrDefault();
            var destinationCity = destination.NearbyCities.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(originCity) || string.IsNullOrWhiteSpace(destinationCity))
            {
                healths.Add(new CorridorHealth
                {
                    Code = corridor.Code,
                    OpenLoadCount = 0,
                    Truncated = false,
                    OriginCity = originCity ?? origin.Code,
                    DestinationCity = destinationCity ?? destination.Code,
                });
                continue;
            }

            LtlSearchResponse response;
            try
            {
                response = await _loads.SearchAsync(new LtlSearchQuery
                {
                    OriginCity = originCity,
                    DestinationCity = destinationCity,
                    Page = 0,
                    // PageSize=1 keeps Alvys payloads tiny — we only need Total.
                    PageSize = 1,
                }, ct);
            }
            catch (Exception)
            {
                // A degraded Alvys read must not take the whole picker down. Return a null-
                // signal for this corridor and let the caller show "unknown" honestly.
                healths.Add(new CorridorHealth
                {
                    Code = corridor.Code,
                    OpenLoadCount = null,
                    Truncated = false,
                    OriginCity = originCity,
                    DestinationCity = destinationCity,
                });
                continue;
            }

            // First open load on the canonical lane, used purely as a default seed hint so the
            // UI can populate the pilot queue on tab-load without app-settings. Honest: null when
            // the lane is empty. PageSize=1 already fetched it, so no extra Alvys read.
            var seed = response.Items.FirstOrDefault();

            healths.Add(new CorridorHealth
            {
                Code = corridor.Code,
                OpenLoadCount = response.Total,
                Truncated = response.Truncated,
                OriginCity = originCity,
                DestinationCity = destinationCity,
                SeedLoadId = seed?.Id,
                SeedLoadNumber = seed?.LoadNumber,
            });
        }

        return Ok(healths);
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
