using System.Globalization;
using System.Text;
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
    TimeProvider clock)
{
    private readonly LtlLoadService _loads = loads;
    private readonly ConsolidationOptions _opts = options.Value;
    private readonly LtlOptions _ltl = ltlOptions.Value;
    private readonly TimeProvider _clock = clock;

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
            var tier = ResolveTier(sibling.CustomerName);
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
                CustomerTier = tier,
                Cautions = cautions,
            });
        }

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

        var combinedRevenue = ComputeCombinedRevenue(parent, siblings);
        var linehaulMiles = parent.Mileage;
        var combinedRpm =
            combinedRevenue is not null
            && linehaulMiles is not null
            && linehaulMiles > 0
                ? Math.Round(combinedRevenue.Value / linehaulMiles.Value, 2)
                : (decimal?)null;

        var previewId = $"plan-{_clock.GetUtcNow():yyyy-MM-dd-HHmmss}-{Random.Shared.Next(1000, 9999)}";
        var tripReferenceValue = $"LTL={parent.LoadNumber ?? parent.Id}";
        var mainLoadIdReferenceValue = parent.LoadNumber ?? parent.Id;

        var clickCard = new ConsolidationClickCard
        {
            PlainText = BuildClickCardText(
                parent, siblings, combinedRevenue, linehaulMiles, combinedRpm,
                corridor.Code, tripReferenceValue, mainLoadIdReferenceValue),
            TripReferenceValue = tripReferenceValue,
            MainLoadIdReferenceValue = mainLoadIdReferenceValue,
        };

        return new ConsolidationPlanResponse
        {
            PreviewId = previewId,
            CorridorCode = corridor.Code,
            Parent = parent,
            Siblings = siblings,
            CombinedRevenue = combinedRevenue,
            LinehaulMiles = linehaulMiles,
            CombinedRevenuePerMile = combinedRpm,
            ClickCard = clickCard,
            Blockers = blockers,
        };
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

    private CustomerConsolidationTier ResolveTier(string? customerName)
    {
        if (string.IsNullOrWhiteSpace(customerName)) return CustomerConsolidationTier.Unknown;
        var policy = _opts.CustomerPolicies.FirstOrDefault(
            p => string.Equals(p.Customer, customerName, StringComparison.OrdinalIgnoreCase));
        return policy?.Tier ?? CustomerConsolidationTier.Unknown;
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
        if (combinedRevenue is not null)
        {
            sb.Append("Projected combined revenue: $").AppendLine(combinedRevenue.Value.ToString("N2", us));
        }
        else
        {
            sb.AppendLine("Projected combined revenue: — (revenue missing on one or more loads)");
        }
        if (linehaulMiles is not null)
        {
            sb.Append("Projected linehaul miles:   ").AppendLine(linehaulMiles.Value.ToString("N0", us));
        }
        else
        {
            sb.AppendLine("Projected linehaul miles:   — (parent mileage missing)");
        }
        if (combinedRpm is not null)
        {
            sb.Append("Projected combined RPM:     $").Append(combinedRpm.Value.ToString("N2", us)).AppendLine(" / mi");
        }
        else
        {
            sb.AppendLine("Projected combined RPM:     — (needs both revenue and mileage)");
        }

        return sb.ToString();
    }
}
