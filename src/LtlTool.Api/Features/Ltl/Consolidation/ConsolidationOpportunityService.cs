using LtlTool.Api.Features.Integrations.Alvys;

namespace LtlTool.Api.Features.Ltl.Consolidation;

/// <summary>
/// Finds high-confidence same-customer / same-day / same-corridor consolidation opportunities
/// directly from live Alvys load-search data. No values are imputed: loads with missing
/// locations, pickup dates, linehaul, mileage, customer id, or load number are excluded.
/// </summary>
public sealed class ConsolidationOpportunityService(IAlvysClient alvys, TimeProvider clock)
{
    private const int PageSize = 100;
    private const int PagesToFetch = 3;
    private const string DataSource = "Alvys va336 (live)";

    public async Task<ConsolidationOpportunitiesResponse> FindOpportunitiesAsync(
        int limit,
        int lookbackDays,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var effectiveLimit = Math.Clamp(limit, 1, 50);
        var effectiveLookbackDays = Math.Clamp(lookbackDays, 1, 90);

        // Fetch recent delivered loads from Alvys. We deliberately omit DateRange here — the
        // Alvys /loads/search endpoint returns most-recent-first, so paging 3 x 100 already
        // gives us the ~300 most-recent delivered loads. Filtering by pickup date
        // client-side keeps the request shape identical to every other successful call.
        var rawLoads = new List<AlvysLoad>(PageSize * PagesToFetch);
        var cutoff = now.AddDays(-effectiveLookbackDays);
        for (var page = 0; page < PagesToFetch; page++)
        {
            AlvysLoadsResponse response;
            try
            {
                response = await alvys.SearchLoadsAsync(new LoadSearchRequest
                {
                    Page = page,
                    PageSize = PageSize,
                    Status = ["Delivered"],
                }, ct);
            }
            catch
            {
                // Alvys transport error — return what we have so far instead of 500ing.
                break;
            }

            if (response.Items.Count == 0) break;
            rawLoads.AddRange(response.Items);
            if (response.Items.Count < PageSize) break;
        }

        // Client-side lookback filter (uses ScheduledPickupAt when present).
        rawLoads = rawLoads
            .Where(l => l.ScheduledPickupAt is null || l.ScheduledPickupAt.Value >= cutoff)
            .ToList();

        var eligible = rawLoads
            .Select(TryBuildEligibleLoad)
            .Where(l => l is not null)
            .Select(l => l!)
            .ToList();

        var groups = eligible
            .GroupBy(l => new OpportunityGroupKey(
                l.OriginState,
                l.DestinationState,
                l.PickupDate,
                l.CustomerId.ToUpperInvariant()))
            .Where(g => g.Count() >= 2)
            .ToList();

        var totalPairsFound = groups.Sum(g =>
        {
            var n = g.Count();
            return n * (n - 1) / 2;
        });

        var opportunities = groups
            .Select(BuildOpportunity)
            .Where(o => o.ParentLinehaulMiles > 0 && o.Siblings.Count > 0)
            .OrderByDescending(o => o.ProjectedUplift)
            .ThenByDescending(o => o.CombinedRevenue)
            .Take(effectiveLimit)
            .Select((o, index) => o with { Rank = index + 1 })
            .ToList();

        return new ConsolidationOpportunitiesResponse
        {
            Opportunities = opportunities,
            TotalScanned = rawLoads.Count,
            TotalPairsFound = totalPairsFound,
            GeneratedAt = now,
            DataSource = DataSource,
        };
    }

    private static ConsolidationOpportunity BuildOpportunity(IEnumerable<EligibleLoad> group)
    {
        var loads = group
            .OrderByDescending(l => l.LinehaulAmount)
            .ThenBy(l => l.LoadNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var parent = loads[0];
        var siblings = loads.Skip(1).ToList();
        var combinedRevenue = loads.Sum(l => l.LinehaulAmount);
        var projectedUplift = siblings.Sum(l => l.LinehaulAmount);
        var combinedRpm = parent.Miles > 0 ? combinedRevenue / parent.Miles : 0m;

        return new ConsolidationOpportunity
        {
            Rank = 0,
            OriginState = parent.OriginState,
            DestinationState = parent.DestinationState,
            OriginCity = parent.OriginCity,
            DestinationCity = parent.DestinationCity,
            PickupDate = parent.PickupDate,
            CustomerName = parent.CustomerName,
            CombinedRevenue = combinedRevenue,
            ParentLinehaulMiles = parent.Miles,
            CombinedRpm = combinedRpm,
            ProjectedUplift = projectedUplift,
            Parent = ToDto(parent),
            Siblings = siblings.Select(ToDto).ToList(),
        };
    }

    private static EligibleLoad? TryBuildEligibleLoad(AlvysLoad load)
    {
        if (string.IsNullOrWhiteSpace(load.Id)
            || string.IsNullOrWhiteSpace(load.LoadNumber)
            || string.IsNullOrWhiteSpace(load.CustomerId)
            || load.ScheduledPickupAt is null
            || load.Linehaul is null or <= 0
            || load.CustomerMileage is null or <= 0)
        {
            return null;
        }

        var pickup = FindStop(load.Stops, isPickup: true);
        var delivery = FindStop(load.Stops, isPickup: false);
        if (!HasCityState(pickup?.Address) || !HasCityState(delivery?.Address))
            return null;

        var linehaul = load.Linehaul.Value;
        var miles = load.CustomerMileage.Value;

        return new EligibleLoad(
            LoadNumber: load.LoadNumber!,
            LoadId: load.Id,
            CustomerId: load.CustomerId!,
            CustomerName: load.CustomerName ?? "",
            OriginCity: pickup!.Address!.City!.Trim(),
            OriginState: pickup.Address.State!.Trim().ToUpperInvariant(),
            DestinationCity: delivery!.Address!.City!.Trim(),
            DestinationState: delivery.Address.State!.Trim().ToUpperInvariant(),
            PickupDate: DateOnly.FromDateTime(load.ScheduledPickupAt.Value.Date),
            LinehaulAmount: linehaul,
            Miles: miles,
            Rpm: linehaul / miles,
            WeightPounds: load.Weight);
    }

    private static AlvysLoadStop? FindStop(IReadOnlyList<AlvysLoadStop>? stops, bool isPickup)
    {
        if (stops is null || stops.Count == 0) return null;

        var named = stops
            .OrderBy(s => s.Sequence ?? int.MaxValue)
            .FirstOrDefault(s =>
                !string.IsNullOrWhiteSpace(s.StopType)
                && s.StopType.Contains(isPickup ? "pickup" : "delivery", StringComparison.OrdinalIgnoreCase));

        if (named is not null) return named;

        return isPickup
            ? stops.OrderBy(s => s.Sequence ?? int.MaxValue).FirstOrDefault()
            : stops.OrderByDescending(s => s.Sequence ?? int.MinValue).FirstOrDefault();
    }

    private static bool HasCityState(AlvysAddress? address) =>
        !string.IsNullOrWhiteSpace(address?.City)
        && !string.IsNullOrWhiteSpace(address.State);

    private static ConsolidationOpportunityLoad ToDto(EligibleLoad load) => new()
    {
        LoadNumber = load.LoadNumber,
        LoadId = load.LoadId,
        CustomerName = load.CustomerName,
        OriginCity = load.OriginCity,
        OriginState = load.OriginState,
        DestinationCity = load.DestinationCity,
        DestinationState = load.DestinationState,
        LinehaulAmount = load.LinehaulAmount,
        Miles = load.Miles,
        Rpm = load.Rpm,
        WeightPounds = load.WeightPounds,
    };

    private sealed record OpportunityGroupKey(
        string OriginState,
        string DestinationState,
        DateOnly PickupDate,
        string CustomerId);

    private sealed record EligibleLoad(
        string LoadNumber,
        string LoadId,
        string CustomerId,
        string CustomerName,
        string OriginCity,
        string OriginState,
        string DestinationCity,
        string DestinationState,
        DateOnly PickupDate,
        decimal LinehaulAmount,
        decimal Miles,
        decimal Rpm,
        decimal? WeightPounds);
}
