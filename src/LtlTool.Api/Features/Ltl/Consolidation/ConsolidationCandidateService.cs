using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.Consolidation;

/// <summary>
/// Finds consolidation candidates for a seed load along the pilot corridor (Laredo → Dallas
/// in Phase 1). Every evaluation is deterministic and derived from live Alvys data via
/// <see cref="LtlLoadService"/>; missing signals resolve to
/// <see cref="ConsolidationFit.Unknown"/>, never invented.
///
/// <para>
/// The service is read-only against Alvys. It never writes, never fabricates dims, never
/// computes a numeric score. Each candidate carries three per-factor chips (Lane, Timing,
/// Customer) with plain-language rationales so a dispatcher can trace the decision back to
/// the source record.
/// </para>
/// </summary>
public sealed class ConsolidationCandidateService(
    LtlLoadService loads,
    IOptions<ConsolidationOptions> options,
    IOptions<LtlOptions> ltlOptions,
    TimeProvider clock,
    ICustomerLtlPolicyReader policyReader)
{
    private readonly LtlLoadService _loads = loads;
    private readonly ConsolidationOptions _opts = options.Value;
    private readonly LtlOptions _ltl = ltlOptions.Value;
    private readonly TimeProvider _clock = clock;
    private readonly ICustomerLtlPolicyReader _policyReader = policyReader;

    /// <summary>
    /// Returns ranked consolidation candidates for the given seed load along the specified
    /// corridor. If the seed cannot be resolved, returns a response with a null Seed and an
    /// empty candidate list; if the corridor is not configured, throws an
    /// <see cref="InvalidOperationException"/> because it indicates a config bug the SPA should
    /// surface as a 400.
    /// </summary>
    public async Task<ConsolidationCandidateResponse> GetCandidatesAsync(
        string seedIdOrNumber,
        string corridorCode,
        CancellationToken ct)
    {
        var corridor = FindCorridor(corridorCode)
            ?? throw new InvalidOperationException($"Unknown corridor: {corridorCode}");

        var origin = FindWarehouse(corridor.OriginWarehouseCode);
        var destination = FindWarehouse(corridor.DestinationWarehouseCode);
        if (origin is null || destination is null)
        {
            throw new InvalidOperationException(
                $"Corridor '{corridor.Code}' references unknown warehouses.");
        }

        var seed = await _loads.GetDetailAsync(seedIdOrNumber, ct);
        if (seed is null)
        {
            return new ConsolidationCandidateResponse
            {
                CorridorCode = corridor.Code,
                Seed = null,
                Candidates = [],
                ScanTruncated = false,
            };
        }

        // Sweep the load list once through the shared search pipeline; then filter locally so
        // corridor + candidate ranking are deterministic and testable without extra Alvys calls.
        var search = await _loads.SearchAsync(
            new LtlSearchQuery
            {
                PageSize = Math.Max(_ltl.AlvysPageSize, 100),
                Page = 1,
            },
            ct);

        var candidates = new List<ConsolidationCandidate>();
        foreach (var summary in search.Items)
        {
            // A load cannot be its own sibling.
            if (string.Equals(summary.Id, seed.Id, StringComparison.OrdinalIgnoreCase)) continue;

            var candidate = await EvaluateAsync(seed, summary, corridor, origin, destination, ct);
            if (candidate is not null) candidates.Add(candidate);
        }

        // Non-blocked first, then by absolute pickup-timing delta to the seed.
        var ordered = candidates
            .OrderBy(c => c.IsBlocked)
            .ThenBy(c => TimingDeltaMinutes(seed, c))
            .Take(_opts.MaxCandidatesReturned)
            .ToArray();

        return new ConsolidationCandidateResponse
        {
            CorridorCode = corridor.Code,
            Seed = seed,
            Candidates = ordered,
            ScanTruncated = search.Truncated,
        };
    }

    private ConsolidationCorridorOptions? FindCorridor(string code) =>
        _opts.Corridors.FirstOrDefault(
            c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));

    private ConsolidationWarehouseOptions? FindWarehouse(string code) =>
        _opts.Warehouses.FirstOrDefault(
            w => string.Equals(w.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Evaluate a single load as a candidate against the seed. Returns <c>null</c> when the
    /// load is not in the corridor at all (so it never appears in the list).
    /// </summary>
    private async Task<ConsolidationCandidate?> EvaluateAsync(
        LtlLoadSummary seed,
        LtlLoadSummary candidate,
        ConsolidationCorridorOptions corridor,
        ConsolidationWarehouseOptions origin,
        ConsolidationWarehouseOptions destination,
        CancellationToken ct)
    {
        // Region gate — hard disqualifier before any factor evaluation.
        if (!IsInAllowedRegion(candidate.Origin) || !IsInAllowedRegion(candidate.Destination))
        {
            return null;
        }

        // Corridor gate: origin near the origin warehouse AND destination near the destination
        // warehouse. Loads outside the corridor never appear as candidates in Phase 1 — they
        // are not "blocked", they are simply not this pilot's job.
        if (!IsNear(candidate.Origin, origin) || !IsNear(candidate.Destination, destination))
        {
            return null;
        }

        var laneFit = EvaluateLaneFit(candidate, origin, destination);
        var timingFit = EvaluateTimingFit(seed, candidate, corridor);
        var (customerFit, tier) = await EvaluateCustomerFitAsync(candidate, ct);

        var factors = new[] { laneFit, timingFit, customerFit };
        var blocked = factors.Any(f => f.Fit == ConsolidationFit.Blocked);

        return new ConsolidationCandidate
        {
            LoadId = candidate.Id,
            LoadNumber = candidate.LoadNumber,
            CustomerName = candidate.CustomerName,
            OriginLabel = candidate.Origin?.Label,
            DestinationLabel = candidate.Destination?.Label,
            ScheduledPickupAt = candidate.ScheduledPickupAt,
            ScheduledDeliveryAt = candidate.ScheduledDeliveryAt,
            Revenue = candidate.Revenue,
            WeightLbs = candidate.WeightLbs,
            CorridorCode = corridor.Code,
            Factors = factors,
            IsBlocked = blocked,
            CustomerTier = tier,
        };
    }

    /// <summary>Lane-fit chip: green when both origin and destination are near the corridor warehouses.</summary>
    private static ConsolidationFactor EvaluateLaneFit(
        LtlLoadSummary candidate,
        ConsolidationWarehouseOptions origin,
        ConsolidationWarehouseOptions destination)
    {
        // We already know the load is near the warehouses (corridor gate above); if we got
        // here, lane fit is green. This is the honest, explainable version — we do not fake a
        // "score" out of it.
        var originLabel = candidate.Origin?.Label ?? "unknown origin";
        var destinationLabel = candidate.Destination?.Label ?? "unknown destination";
        return new ConsolidationFactor
        {
            Name = "Lane fit",
            Fit = ConsolidationFit.Good,
            Rationale = $"{originLabel} near {origin.Name}; {destinationLabel} near {destination.Name}.",
        };
    }

    /// <summary>
    /// Timing-fit chip: <see cref="ConsolidationFit.Good"/> when the candidate's pickup is
    /// within the corridor's PickupWindowDays of the seed's pickup;
    /// <see cref="ConsolidationFit.Tight"/> when it's within 2× that window;
    /// <see cref="ConsolidationFit.Blocked"/> when it's beyond 2×;
    /// <see cref="ConsolidationFit.Unknown"/> when either pickup is missing.
    /// </summary>
    private static ConsolidationFactor EvaluateTimingFit(
        LtlLoadSummary seed,
        LtlLoadSummary candidate,
        ConsolidationCorridorOptions corridor)
    {
        if (seed.ScheduledPickupAt is null || candidate.ScheduledPickupAt is null)
        {
            return new ConsolidationFactor
            {
                Name = "Timing fit",
                Fit = ConsolidationFit.Unknown,
                Rationale = "Pickup time missing on seed or candidate — visual verify at dock.",
            };
        }

        var delta = (candidate.ScheduledPickupAt.Value - seed.ScheduledPickupAt.Value).Duration();
        var window = TimeSpan.FromDays(corridor.PickupWindowDays);

        if (delta <= window)
        {
            return new ConsolidationFactor
            {
                Name = "Timing fit",
                Fit = ConsolidationFit.Good,
                Rationale = $"Pickup within {corridor.PickupWindowDays}d of seed load.",
            };
        }

        if (delta <= window + window)
        {
            return new ConsolidationFactor
            {
                Name = "Timing fit",
                Fit = ConsolidationFit.Tight,
                Rationale =
                    $"Pickup {(int)delta.TotalDays}d from seed — outside {corridor.PickupWindowDays}d " +
                    "window; dispatcher confirmation recommended.",
            };
        }

        return new ConsolidationFactor
        {
            Name = "Timing fit",
            Fit = ConsolidationFit.Blocked,
            Rationale =
                $"Pickup {(int)delta.TotalDays}d from seed — beyond corridor window; " +
                "cannot combine on one linehaul.",
        };
    }

    /// <summary>
    /// Customer-fit chip: derived from the per-customer policy reader (Alvys customer notes
    /// first, static config fallback). Unknown by default → yellow "confirm with account
    /// owner", never green. See <see cref="CustomerNotesLtlPolicyReader"/> for the notes
    /// convention.
    /// </summary>
    private async Task<(ConsolidationFactor Factor, CustomerConsolidationTier Tier)>
        EvaluateCustomerFitAsync(LtlLoadSummary candidate, CancellationToken ct)
    {
        var tier = await _policyReader.ResolveAsync(candidate.CustomerId, candidate.CustomerName, ct);
        var factor = tier switch
        {
            CustomerConsolidationTier.Allowed => new ConsolidationFactor
            {
                Name = "Customer",
                Fit = ConsolidationFit.Good,
                Rationale = $"{candidate.CustomerName ?? "Customer"} allows consolidation.",
            },
            CustomerConsolidationTier.NotifyRequired => new ConsolidationFactor
            {
                Name = "Customer",
                Fit = ConsolidationFit.Tight,
                Rationale =
                    $"{candidate.CustomerName ?? "Customer"} accepts consolidation with notification " +
                    "to the right people. Confirm before dispatch.",
            },
            CustomerConsolidationTier.Never => new ConsolidationFactor
            {
                Name = "Customer",
                Fit = ConsolidationFit.Blocked,
                Rationale =
                    $"{candidate.CustomerName ?? "Customer"} does not permit consolidation. " +
                    "Blocked.",
            },
            _ => new ConsolidationFactor
            {
                Name = "Customer",
                Fit = ConsolidationFit.Tight,
                Rationale =
                    $"No consolidation policy on file for " +
                    $"{candidate.CustomerName ?? "this customer"} — confirm with account owner.",
            },
        };
        return (factor, tier);
    }

    private bool IsInAllowedRegion(LtlPlace? place)
    {
        if (place is null || string.IsNullOrWhiteSpace(place.State)) return false;
        // Two-letter US state codes are treated as US for the region gate.
        // AllowedRegions defaults to ["US"] which is the only Phase 1 case.
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

    private static double TimingDeltaMinutes(LtlLoadSummary seed, ConsolidationCandidate candidate)
    {
        if (seed.ScheduledPickupAt is null || candidate.ScheduledPickupAt is null)
        {
            return double.MaxValue;
        }
        return (candidate.ScheduledPickupAt.Value - seed.ScheduledPickupAt.Value)
            .Duration()
            .TotalMinutes;
    }
}
