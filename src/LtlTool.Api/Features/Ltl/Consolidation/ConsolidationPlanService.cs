using System.Globalization;
using System.Text;
using LtlTool.Api.Features.Ltl.Optimization;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.Consolidation;

/// <summary>
/// Builds a consolidation plan preview from a parent load + one or more sibling loads.
/// Every returned value is either a live Alvys read (revenue, mileage, origin/destination,
/// customer name) or a static config value (corridor code, customer policy). Nothing writes
/// upstream; nothing is fabricated. When the request would produce an illegal plan (missing
/// parent, sibling outside the corridor, sibling belonging to a Never-consolidate customer)
/// the response's <see cref="ConsolidationPlanResponse.Blockers"/> list is populated and the
/// SPA must not offer the copy-to-Alvys action.
///
/// <para>
/// The click card format is the sanctioned Alvys pattern Poornima walked Holly through at
/// the Phoenix yard visit: parent gets waypoints for sibling deliveries, siblings zero out
/// loaded miles, both carry an LTL trip reference plus a main-load-id reference, and the
/// combined-RPM report filter uses the AND expression. Any drift from that pattern would
/// re-introduce the dummy-load workaround the pilot is designed to replace, so the format
/// is generated deterministically here and not hand-edited by the SPA.
/// </para>
/// </summary>
public sealed class ConsolidationPlanService(
    LtlLoadService loads,
    IOptions<ConsolidationOptions> options,
    IOptions<LtlOptions> ltlOptions,
    TimeProvider clock,
    ICustomerLtlPolicyReader policyReader,
    ITrailerFitService trailerFit,
    IStopSequencer stopSequencer)
{
    private readonly LtlLoadService _loads = loads;
    private readonly ConsolidationOptions _opts = options.Value;
    private readonly LtlOptions _ltl = ltlOptions.Value;
    private readonly TimeProvider _clock = clock;
    private readonly ICustomerLtlPolicyReader _policyReader = policyReader;
    private readonly ITrailerFitService _trailerFit = trailerFit;
    private readonly IStopSequencer _stopSequencer = stopSequencer;

    /// <summary>
    /// Builds a plan preview. Returns 400-shaped errors as <see cref="InvalidOperationException"/>
    /// (the controller converts them to <c>400 Bad Request</c>). Returns a response with
    /// non-empty Blockers when the plan is well-formed but illegal on inspection (e.g. a
    /// Never-consolidate customer).
    /// </summary>
    public async Task<ConsolidationPlanResponse> BuildAsync(
        ConsolidationPlanRequest request,
        CancellationToken ct)
    {
        if (request is null) throw new InvalidOperationException("Request body is required.");
        if (string.IsNullOrWhiteSpace(request.ParentLoadId))
        {
            throw new InvalidOperationException("parentLoadId is required.");
        }
        if (request.SiblingLoadIds is null || request.SiblingLoadIds.Count == 0)
        {
            throw new InvalidOperationException("At least one sibling load id is required.");
        }

        var corridorCode = string.IsNullOrWhiteSpace(request.CorridorCode)
            ? "LAREDO_TO_DALLAS"
            : request.CorridorCode;
        var corridor = _opts.Corridors.FirstOrDefault(
            c => string.Equals(c.Code, corridorCode, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Unknown corridor: {corridorCode}");

        var originWarehouse = _opts.Warehouses.FirstOrDefault(
            w => string.Equals(w.Code, corridor.OriginWarehouseCode, StringComparison.OrdinalIgnoreCase));
        var destinationWarehouse = _opts.Warehouses.FirstOrDefault(
            w => string.Equals(w.Code, corridor.DestinationWarehouseCode, StringComparison.OrdinalIgnoreCase));
        if (originWarehouse is null || destinationWarehouse is null)
        {
            throw new InvalidOperationException(
                $"Corridor '{corridor.Code}' references unknown warehouses.");
        }

        var parent = await _loads.GetDetailAsync(request.ParentLoadId, ct);
        if (parent is null)
        {
            throw new InvalidOperationException(
                $"Parent load '{request.ParentLoadId}' could not be resolved from Alvys.");
        }

        // Dedupe sibling ids and drop the parent from siblings if a client accidentally sent it.
        var siblingIds = request.SiblingLoadIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Where(id => !string.Equals(id, parent.Id, StringComparison.OrdinalIgnoreCase))
            .Where(id => !string.Equals(id, parent.LoadNumber, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (siblingIds.Length == 0)
        {
            throw new InvalidOperationException(
                "At least one sibling load must be different from the parent.");
        }

        var blockers = new List<string>();
        var siblings = new List<ConsolidationPlanSibling>();

        foreach (var siblingId in siblingIds)
        {
            var sibling = await _loads.GetDetailAsync(siblingId, ct);
            if (sibling is null)
            {
                blockers.Add($"Sibling load '{siblingId}' could not be resolved from Alvys.");
                continue;
            }

            // Region gate.
            if (!IsInAllowedRegion(sibling.Origin) || !IsInAllowedRegion(sibling.Destination))
            {
                blockers.Add(
                    $"Sibling '{sibling.LoadNumber ?? sibling.Id}' is outside allowed regions " +
                    $"({string.Join(", ", _opts.AllowedRegions)}).");
                continue;
            }

            // Corridor gate.
            if (!IsNear(sibling.Origin, originWarehouse) || !IsNear(sibling.Destination, destinationWarehouse))
            {
                blockers.Add(
                    $"Sibling '{sibling.LoadNumber ?? sibling.Id}' is not on the " +
                    $"{corridor.Code} corridor (origin/destination outside warehouse radius).");
                continue;
            }

            // Customer policy gate.
            var policy = await _policyReader.ResolveAsync(sibling.CustomerId, sibling.CustomerName, ct);
            var tier = policy.Tier;
            if (tier == CustomerConsolidationTier.Never)
            {
                blockers.Add(
                    $"Customer '{sibling.CustomerName ?? "unknown"}' does not permit " +
                    $"consolidation (sibling {sibling.LoadNumber ?? sibling.Id}).");
                continue;
            }

            var cautions = BuildCautions(sibling, tier);

            siblings.Add(new ConsolidationPlanSibling
            {
                LoadId = sibling.Id,
                LoadNumber = sibling.LoadNumber,
                CustomerName = sibling.CustomerName,
                OriginLabel = sibling.Origin?.Label,
                DestinationLabel = sibling.Destination?.Label,
                ScheduledPickupAt = sibling.ScheduledPickupAt,
                ScheduledDeliveryAt = sibling.ScheduledDeliveryAt,
                Revenue = sibling.Revenue,
                WeightLbs = sibling.WeightLbs,
                DriverTripRate = sibling.DriverTripRate,
                LoadedMiles = sibling.LoadedMiles,
                CustomerTier = tier,
                CustomerPolicySource = policy.Source,
                Cautions = cautions,
            });
        }

        // Stop sequencing (Phase 2 M3): order the sibling delivery waypoints for the click card.
        // With Ltl:Optimization:Solver:Enabled off, the NullStopSequencer preserves input order;
        // with it on, the OR-Tools sequencer reorders when stop coordinates are available (today
        // Alvys exposes city/state only, so the order is preserved and honestly reported as such).
        var (siblingOrder, stopsOptimized) = await SequenceSiblingsAsync(parent, siblings, ct);
        siblings = siblingOrder;

        // Parent must itself sit on the corridor.
        if (!IsInAllowedRegion(parent.Origin) || !IsInAllowedRegion(parent.Destination))
        {
            blockers.Add(
                $"Parent '{parent.LoadNumber ?? parent.Id}' is outside allowed regions " +
                $"({string.Join(", ", _opts.AllowedRegions)}).");
        }
        if (!IsNear(parent.Origin, originWarehouse) || !IsNear(parent.Destination, destinationWarehouse))
        {
            blockers.Add(
                $"Parent '{parent.LoadNumber ?? parent.Id}' is not on the " +
                $"{corridor.Code} corridor.");
        }

        // Combined revenue = customer-facing money on the plan (parent + siblings), still shown
        // to the operator because it's the total the customer owes for the moves being combined.
        var combinedRevenue = ComputeCombinedRevenue(parent, siblings);

        // Combined RPM math — corrected 2026-07-18 per Reuben transcript (15:55, 33:06) and
        // empirical findings (see docs/ALVYS_API_DECISIONS.md).
        //
        //  * Prior (bug): CombinedRpm = combinedRevenue / parent.Mileage. That mixed
        //    customer-rate money with customer-billing miles — an inflated billing RPM that
        //    didn't match the driver-facing number Junior/Holly/Brian actually care about.
        //
        //  * Fixed: CombinedRpm = combinedDriverTripValue / parent.LoadedMiles.
        //    Both inputs are driver-facing, per Reuben: "take the driver's rate and divide it
        //    by the mileage." Empirically verified: Trip.TripValue.Amount and
        //    Trip.LoadedMileage.Distance.Value on the trip response.
        //
        // Consolidation Planner economics panel now names both LinehaulMiles (informational,
        // preserves the customer-billing miles for context) AND DriverLoadedMiles (the number
        // the RPM was actually computed against).
        var linehaulMiles = parent.Mileage;
        var driverLoadedMiles = parent.LoadedMiles;
        var combinedDriverTripValue = ComputeCombinedDriverTripValue(parent, siblings);
        var combinedRpm =
            combinedDriverTripValue is not null
            && driverLoadedMiles is > 0
                ? Math.Round(combinedDriverTripValue.Value / driverLoadedMiles.Value, 2)
                : (decimal?)null;

        var previewId = $"plan-{_clock.GetUtcNow():yyyy-MM-dd-HHmmss}-{Random.Shared.Next(1000, 9999)}";
        var tripReferenceValue = $"LTL={parent.LoadNumber ?? parent.Id}";
        var mainLoadIdReferenceValue = parent.LoadNumber ?? parent.Id;

        var clickCard = new ConsolidationClickCard
        {
            PlainText = BuildClickCardText(
                parent, siblings,
                combinedRevenue, linehaulMiles,
                combinedDriverTripValue, driverLoadedMiles, combinedRpm,
                corridor.Code, tripReferenceValue, mainLoadIdReferenceValue),
            TripReferenceValue = tripReferenceValue,
            MainLoadIdReferenceValue = mainLoadIdReferenceValue,
        };

        var trailerFit = await EvaluateTrailerFitAsync(parent, siblings, ct);

        return new ConsolidationPlanResponse
        {
            PreviewId = previewId,
            CorridorCode = corridor.Code,
            Parent = parent,
            Siblings = siblings,
            CombinedRevenue = combinedRevenue,
            LinehaulMiles = linehaulMiles,
            DriverLoadedMiles = driverLoadedMiles,
            CombinedDriverTripValue = combinedDriverTripValue,
            CombinedRevenuePerMile = combinedRpm,
            ClickCard = clickCard,
            Blockers = blockers,
            TrailerFit = trailerFit,
            StopSequence = siblings.Select(s => s.LoadNumber ?? s.LoadId).ToList(),
            StopsOptimized = stopsOptimized,
        };
    }

    /// <summary>
    /// Runs the trailer-fit engine over the combined load (parent + corridor-valid siblings) and
    /// projects it for the SPA. Returns null when the engine is disabled (the <c>NullTrailerFitService</c>
    /// is active) so the plan-detail page shows "verify at dock" rather than a fabricated verdict.
    /// The engine itself never throws for a business reason and degrades on sidecar failure, so this
    /// call never fails plan generation. Every input is an Alvys-derived aggregate already resolved
    /// above — no extra Alvys read, no invented dimension.
    /// </summary>
    private async Task<ConsolidationTrailerFit?> EvaluateTrailerFitAsync(
        LtlLoadSummary parent,
        IReadOnlyList<ConsolidationPlanSibling> siblings,
        CancellationToken ct)
    {
        if (!_trailerFit.IsEnabled) return null;

        var items = new List<TrailerFitItem>
        {
            new(parent.LoadNumber ?? parent.Id, parent.WeightLbs, null, parent.Volume),
        };
        foreach (var s in siblings)
        {
            items.Add(new TrailerFitItem(s.LoadNumber ?? s.LoadId, s.WeightLbs, null, null));
        }

        // No assigned trailer at preview time — the engine falls back to its configured standard
        // 53' dry-van capacity (an equipment spec constant, not Alvys operational data).
        var request = new TrailerFitRequest(new TrailerCapacitySpec(null, null, null), items);
        var result = await _trailerFit.EvaluateAsync(request, ct);

        return new ConsolidationTrailerFit
        {
            Verdict = result.Verdict.ToString(),
            Rationale = result.Rationale,
            EstimatedFit = result.EstimatedFit,
            LinearFeet = result.LinearFeet,
            WeightUtilization = result.WeightUtilization,
            CubeUtilization = result.CubeUtilization,
            TotalWeightLbs = result.TotalWeightLbs,
            TrailerMaxWeightLbs = result.TrailerMaxWeightLbs,
            TotalPallets = result.TotalPallets,
            TrailerMaxPallets = result.TrailerMaxPallets,
            CapacityExceeded = result.CapacityExceeded,
            WeightUnknown = result.WeightUnknown,
        };
    }

    /// <summary>
    /// Orders the sibling delivery waypoints via <see cref="IStopSequencer"/>, anchored at the
    /// parent origin. Returns the reordered siblings and whether the sequencer actually reordered
    /// them (false when the input order was preserved — the honest default without stop coordinates).
    /// </summary>
    private async Task<(List<ConsolidationPlanSibling> Ordered, bool Optimized)> SequenceSiblingsAsync(
        LtlLoadSummary parent,
        List<ConsolidationPlanSibling> siblings,
        CancellationToken ct)
    {
        if (siblings.Count < 2) return (siblings, false);

        var stops = new List<StopToSequence>
        {
            new(parent.Id, parent.Destination?.City, parent.Destination?.State, null, null),
        };
        stops.AddRange(siblings.Select(s =>
        {
            var city = s.DestinationLabel?.Split(',')[0].Trim();
            return new StopToSequence(s.LoadId, city, null, null, null);
        }));

        var result = await _stopSequencer.SequenceAsync(new StopSequenceRequest(stops), ct);
        if (!result.Optimized) return (siblings, false);

        var byId = siblings.ToDictionary(s => s.LoadId, StringComparer.OrdinalIgnoreCase);
        var ordered = result.OrderedStopRefs
            .Where(r => byId.ContainsKey(r)) // drop the parent anchor / any unknown ref
            .Select(r => byId[r])
            .ToList();

        // Guard against a sequencer dropping a stop: fall back to input order if counts differ.
        return ordered.Count == siblings.Count ? (ordered, true) : (siblings, false);
    }

    private static decimal? ComputeCombinedRevenue(
        LtlLoadSummary parent,
        IReadOnlyList<ConsolidationPlanSibling> siblings)
    {
        var anyRevenue = parent.Revenue is not null || siblings.Any(s => s.Revenue is not null);
        if (!anyRevenue) return null;
        var total = parent.Revenue ?? 0m;
        foreach (var s in siblings) total += s.Revenue ?? 0m;
        return total;
    }

    /// <summary>
    /// Sums driver trip rate (<c>Trip.TripValue.Amount</c>) across parent + siblings. Returns
    /// null when nobody has a driver rate on file — the combined-RPM math must then also
    /// stay null instead of guessing.
    /// </summary>
    private static decimal? ComputeCombinedDriverTripValue(
        LtlLoadSummary parent,
        IReadOnlyList<ConsolidationPlanSibling> siblings)
    {
        var anyRate = parent.DriverTripRate is not null || siblings.Any(s => s.DriverTripRate is not null);
        if (!anyRate) return null;
        var total = parent.DriverTripRate ?? 0m;
        foreach (var s in siblings) total += s.DriverTripRate ?? 0m;
        return total;
    }



    private static IReadOnlyList<string> BuildCautions(LtlLoadSummary sibling, CustomerConsolidationTier tier)
    {
        var cautions = new List<string>();
        if (sibling.WeightLbs is null)
        {
            cautions.Add(
                $"{sibling.LoadNumber ?? sibling.Id}: weight missing — visual verify at Laredo dock.");
        }
        if (tier == CustomerConsolidationTier.NotifyRequired)
        {
            cautions.Add(
                $"{sibling.CustomerName ?? "Customer"} requires notification to the right people " +
                "before consolidation. Confirm account owner sign-off.");
        }
        else if (tier == CustomerConsolidationTier.Unknown)
        {
            cautions.Add(
                $"No consolidation policy on file for {sibling.CustomerName ?? "this customer"} — " +
                "confirm with account owner before dispatch.");
        }
        return cautions;
    }

    private bool IsInAllowedRegion(LtlPlace? place)
    {
        if (place is null || string.IsNullOrWhiteSpace(place.State)) return false;
        if (_opts.AllowedRegions.Contains("US", StringComparer.OrdinalIgnoreCase)
            && place.State.Length == 2)
        {
            return true;
        }
        return _opts.AllowedRegions.Contains(place.State, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsNear(LtlPlace? place, ConsolidationWarehouseOptions warehouse)
    {
        if (place is null) return false;
        if (!string.IsNullOrWhiteSpace(place.State)
            && string.Equals(place.State, warehouse.State, StringComparison.OrdinalIgnoreCase))
        {
            if (warehouse.NearbyCities.Count == 0) return true;
            if (string.IsNullOrWhiteSpace(place.City)) return true;
            foreach (var city in warehouse.NearbyCities)
            {
                if (place.City.Contains(city, StringComparison.OrdinalIgnoreCase)) return true;
                if (city.Contains(place.City, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    private static string BuildClickCardText(
        LtlLoadSummary parent,
        IReadOnlyList<ConsolidationPlanSibling> siblings,
        decimal? combinedRevenue,
        decimal? linehaulMiles,
        decimal? combinedDriverTripValue,
        decimal? driverLoadedMiles,
        decimal? combinedRpm,
        string corridorCode,
        string tripReferenceValue,
        string mainLoadIdReferenceValue)
    {
        var us = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("LTL CONSOLIDATION PLAN");
        sb.Append("Corridor: ").AppendLine(corridorCode);
        sb.AppendLine();

        var parentLabel = parent.LoadNumber ?? parent.Id;
        var parentDestination = parent.Destination?.Label ?? "unknown destination";
        sb.Append("Parent load:    ").Append(parentLabel);
        sb.Append("    ").Append(parent.CustomerName ?? "customer unknown")
          .Append(" → ").AppendLine(parentDestination);
        foreach (var s in siblings)
        {
            var sLabel = s.LoadNumber ?? s.LoadId;
            var sDest = s.DestinationLabel ?? "unknown destination";
            sb.Append("Sibling load:   ").Append(sLabel);
            sb.Append("    ").Append(s.CustomerName ?? "customer unknown")
              .Append(" → ").AppendLine(sDest);
        }
        sb.AppendLine();
        sb.AppendLine("Do this in Alvys:");
        sb.AppendLine();

        sb.Append("  1. Open parent load ").Append(parentLabel).AppendLine(" in Alvys");
        sb.AppendLine("     → Assign driver, truck, trailer as normal");
        sb.AppendLine("     → Add stop → Waypoint for each sibling delivery");
        sb.AppendLine("     → In Trip References, add:");
        sb.Append("         ").AppendLine(tripReferenceValue);
        sb.Append("         Main Load Id = ").AppendLine(mainLoadIdReferenceValue);
        sb.AppendLine();

        var step = 2;
        foreach (var s in siblings)
        {
            var sLabel = s.LoadNumber ?? s.LoadId;
            sb.Append("  ").Append(step).Append(". Open sibling load ").Append(sLabel).AppendLine(" in Alvys");
            sb.Append("     → Assign the SAME driver, truck, trailer as ").AppendLine(parentLabel);
            sb.AppendLine("     → Dispatch language panel (left) → Loaded miles → set to 0");
            sb.AppendLine("     → In Trip References, add:");
            sb.Append("         ").AppendLine(tripReferenceValue);
            sb.Append("         Main Load Id = ").AppendLine(mainLoadIdReferenceValue);
            sb.AppendLine();
            step++;
        }

        sb.Append("  ").Append(step).AppendLine(". View combined RPM in Alvys");
        sb.AppendLine("     → Trips report → Filter →");
        sb.Append("         Trip References contain \"").Append(tripReferenceValue).AppendLine("\" AND");
        sb.Append("         \"Main Load Id = ").Append(mainLoadIdReferenceValue).AppendLine("\"");
        sb.AppendLine();

        sb.AppendLine("─────────────────────────────────────────");
        // Customer-side (billing) totals — shown for operator context so leadership can also
        // see the customer money on the plan, but NOT the numbers RPM is computed against.
        if (combinedRevenue is not null)
        {
            sb.Append("Combined customer revenue:  $").AppendLine(combinedRevenue.Value.ToString("N2", us));
        }
        else
        {
            sb.AppendLine("Combined customer revenue:  — (revenue missing on one or more loads)");
        }
        if (linehaulMiles is not null)
        {
            sb.Append("Customer linehaul miles:    ").AppendLine(linehaulMiles.Value.ToString("N0", us));
        }
        else
        {
            sb.AppendLine("Customer linehaul miles:    — (parent customer mileage missing)");
        }

        sb.AppendLine();
        // Driver-side (dispatch) totals — the numbers RPM is actually computed against.
        // Per Reuben 2026-07-17 sync (33:06): driver RPM is driver rate ÷ loaded miles.
        if (combinedDriverTripValue is not null)
        {
            sb.Append("Combined driver trip value: $").AppendLine(combinedDriverTripValue.Value.ToString("N2", us));
        }
        else
        {
            sb.AppendLine("Combined driver trip value: — (Trip.TripValue missing on one or more loads)");
        }
        if (driverLoadedMiles is not null)
        {
            sb.Append("Parent loaded miles:        ").AppendLine(driverLoadedMiles.Value.ToString("N0", us));
        }
        else
        {
            sb.AppendLine("Parent loaded miles:        — (Trip.LoadedMileage missing on parent)");
        }
        if (combinedRpm is not null)
        {
            sb.Append("Combined driver RPM:        $")
              .Append(combinedRpm.Value.ToString("N2", us))
              .AppendLine(" / mi");
        }
        else
        {
            sb.AppendLine("Combined driver RPM:        — (needs both combined driver trip value and parent loaded miles)");
        }

        return sb.ToString();
    }
}
