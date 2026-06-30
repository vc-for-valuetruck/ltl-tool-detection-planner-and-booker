using LtlTool.Api.Features.Integrations.Alvys;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl;

/// <summary>
/// Orchestrates the normalized LTL read model on top of the read-only Alvys client: sweeps
/// loads, normalizes them, and serves the search grid, billing worklist, exception list and
/// single-load detail.
///
/// Alvys-supported filters (status / customer / pickup window) are pushed into the upstream
/// query; the richer decision-support filters (origin/destination, assignment, billing state,
/// exceptions) and all sorting are applied in-memory over the swept, normalized page, because
/// they are derived fields Alvys does not index. The sweep is bounded by
/// <see cref="LtlOptions.MaxLoadsScanned"/>; when it is hit the response is marked
/// <see cref="LtlSearchResponse.Truncated"/> so the UI can say so honestly.
/// </summary>
public sealed class LtlLoadService(
    IAlvysClient alvys, LtlNormalizationService normalizer, IOptions<LtlOptions> options)
{
    private readonly LtlOptions _options = options.Value;

    /// <summary>Normalized, filtered, sorted, paged LTL search.</summary>
    public async Task<LtlSearchResponse> SearchAsync(LtlSearchQuery query, CancellationToken ct)
    {
        var (loads, truncated) = await SweepAsync(BuildUpstreamRequest(query), ct);

        var normalized = loads.Select(l => normalizer.Normalize(l)).ToList();
        var filtered = normalized.Where(s => Matches(s, query));
        var sorted = ApplySort(filtered, query);

        var all = sorted.ToList();
        var pageSize = Math.Clamp(query.PageSize, 1, LtlSearchQuery.MaxPageSize);
        var page = Math.Max(1, query.Page);
        var items = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return new LtlSearchResponse
        {
            Page = page,
            PageSize = pageSize,
            Total = all.Count,
            Items = items,
            Truncated = truncated,
        };
    }

    /// <summary>
    /// Single-load detail, normalized with POD-aware billing (documents fetched) and invoice-aware
    /// billing (invoices fetched by load number). Invoice/document reads degrade to empty on the
    /// read-only client, so detail still renders when those sub-resources are unavailable.
    /// </summary>
    public async Task<LtlLoadSummary?> GetDetailAsync(string idOrNumber, CancellationToken ct)
    {
        var load = await ResolveLoadAsync(idOrNumber, ct);
        if (load is null) return null;

        var loadNumber = load.LoadNumber ?? idOrNumber;
        var documents = await alvys.ListLoadDocumentsAsync(loadNumber, ct);
        var invoices = await FetchInvoicesForLoadAsync(loadNumber, ct);
        return normalizer.Normalize(load, documents, invoices);
    }

    /// <summary>
    /// Fetches invoices linked to a single load number (read-only). Returns an empty list when
    /// none exist or the upstream degrades — never fabricates billing state.
    /// </summary>
    private async Task<IReadOnlyList<AlvysInvoice>> FetchInvoicesForLoadAsync(
        string loadNumber, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(loadNumber)) return [];

        var response = await alvys.SearchInvoicesAsync(
            new InvoiceSearchRequest { Page = 0, PageSize = 50, LoadNumbers = [loadNumber] }, ct);
        return response.Items;
    }

    /// <summary>Raw + documents resolution used by detail and matching.</summary>
    public async Task<AlvysLoad?> ResolveLoadAsync(string idOrNumber, CancellationToken ct)
    {
        // id, loadNumber and orderNumber are all accepted by the Alvys detail lookup; pass the
        // value as all three so the caller doesn't need to know which identifier they hold.
        var lookup = new LoadLookup { Id = idOrNumber, LoadNumber = idOrNumber, OrderNumber = idOrNumber };
        return await alvys.GetLoadAsync(lookup, ct);
    }

    /// <summary>
    /// Billing worklist: normalized loads that still need billing attention (not yet invoiced),
    /// optionally filtered to a single badge, sorted by readiness then pickup.
    /// </summary>
    public async Task<IReadOnlyList<LtlLoadSummary>> BillingWorklistAsync(
        BillingBadge? badge, CancellationToken ct)
    {
        var (loads, _) = await SweepAsync(new LoadSearchRequest { PageSize = _options.AlvysPageSize }, ct);
        var normalized = loads.Select(l => normalizer.Normalize(l));

        return normalized
            .Where(s => !s.Billing.IsAlreadyInvoiced)
            .Where(s => badge is null || s.Billing.Badges.Contains(badge.Value))
            .OrderByDescending(s => s.Billing.IsReadyToBill)
            .ThenBy(s => s.ScheduledPickupAt ?? DateTimeOffset.MaxValue)
            .ToList();
    }

    /// <summary>Loads carrying one or more operational/billing exceptions.</summary>
    public async Task<IReadOnlyList<LtlLoadSummary>> ExceptionsAsync(CancellationToken ct)
    {
        var (loads, _) = await SweepAsync(new LoadSearchRequest { PageSize = _options.AlvysPageSize }, ct);
        return loads
            .Select(l => normalizer.Normalize(l))
            .Where(s => s.HasExceptions)
            .OrderByDescending(s => s.Exceptions.Count(e => e.BlocksBilling))
            .ThenByDescending(s => s.Exceptions.Count)
            .ToList();
    }

    private LoadSearchRequest BuildUpstreamRequest(LtlSearchQuery query)
    {
        var request = new LoadSearchRequest { PageSize = _options.AlvysPageSize };

        if (query.Status is { Count: > 0 })
            request.Status = query.Status;

        if (!string.IsNullOrWhiteSpace(query.Customer)
            && Guid.TryParse(query.Customer, out _))
        {
            // CustomerId is an Alvys id filter; only push it when it looks like an id.
            request.CustomerId = query.Customer;
        }

        if (query.PickupFrom is not null || query.PickupTo is not null)
            request.DateRange = new AlvysDateRange { Start = query.PickupFrom, End = query.PickupTo };

        return request;
    }

    /// <summary>
    /// Pages Alvys loads/search until exhausted or the scan bound is reached. Returns the loads
    /// and whether the bound truncated the sweep.
    /// </summary>
    private async Task<(List<AlvysLoad> Loads, bool Truncated)> SweepAsync(
        LoadSearchRequest seed, CancellationToken ct)
    {
        var loads = new List<AlvysLoad>();
        var pageSize = seed.PageSize > 0 ? seed.PageSize : _options.AlvysPageSize;
        var page = 0;
        var truncated = false;

        while (true)
        {
            var request = CloneSeed(seed, page, pageSize);
            var response = await alvys.SearchLoadsAsync(request, ct);
            if (response.Items.Count == 0) break;

            loads.AddRange(response.Items);

            if (loads.Count >= _options.MaxLoadsScanned)
            {
                truncated = response.Total > loads.Count;
                break;
            }

            // Stop when we've drawn the full reported total or a short final page.
            if (loads.Count >= response.Total || response.Items.Count < pageSize) break;
            page++;
        }

        return (loads, truncated);
    }

    private static LoadSearchRequest CloneSeed(LoadSearchRequest seed, int page, int pageSize) => new()
    {
        Page = page,
        PageSize = pageSize,
        Status = seed.Status,
        CustomerId = seed.CustomerId,
        DateRange = seed.DateRange,
        OrderNumbers = seed.OrderNumbers,
        LoadNumbers = seed.LoadNumbers,
        PONumbers = seed.PONumbers,
    };

    private static bool Matches(LtlLoadSummary s, LtlSearchQuery q)
    {
        if (q.LtlOnly && s.IsLtl != true) return false;
        if (q.Assignment is not null && s.Assignment != q.Assignment) return false;
        if (q.ReadyToBill && !s.Billing.IsReadyToBill) return false;
        if (q.MissingBillingData && s.Billing.MissingFields.Count == 0) return false;
        if (q.ExceptionsOnly && !s.HasExceptions) return false;
        if (q.BillingBadge is not null && !s.Billing.Badges.Contains(q.BillingBadge.Value)) return false;

        if (!string.IsNullOrWhiteSpace(q.OriginState) && !EqualsCi(s.Origin?.State, q.OriginState)) return false;
        if (!string.IsNullOrWhiteSpace(q.DestinationState) && !EqualsCi(s.Destination?.State, q.DestinationState)) return false;
        if (!string.IsNullOrWhiteSpace(q.OriginCity) && !ContainsCi(s.Origin?.City, q.OriginCity)) return false;
        if (!string.IsNullOrWhiteSpace(q.DestinationCity) && !ContainsCi(s.Destination?.City, q.DestinationCity)) return false;
        if (!string.IsNullOrWhiteSpace(q.EquipmentType) && !s.Equipment.Any(e => ContainsCi(e, q.EquipmentType))) return false;

        // Customer free-text (when not an id pushed upstream): match name or id substring.
        if (!string.IsNullOrWhiteSpace(q.Customer) && !Guid.TryParse(q.Customer, out _)
            && !ContainsCi(s.CustomerName, q.Customer) && !ContainsCi(s.CustomerId, q.Customer))
            return false;

        if (!string.IsNullOrWhiteSpace(q.Keyword) && !MatchesKeyword(s, q.Keyword!)) return false;

        // Pickup window when the upstream filter wasn't usable (open-ended re-check is harmless).
        if (q.PickupFrom is not null && s.ScheduledPickupAt is not null && s.ScheduledPickupAt < q.PickupFrom) return false;
        if (q.PickupTo is not null && s.ScheduledPickupAt is not null && s.ScheduledPickupAt > q.PickupTo) return false;

        // Delivery window (derived field — always applied in-memory).
        if (q.DeliveryFrom is not null && s.ScheduledDeliveryAt is not null && s.ScheduledDeliveryAt < q.DeliveryFrom) return false;
        if (q.DeliveryTo is not null && s.ScheduledDeliveryAt is not null && s.ScheduledDeliveryAt > q.DeliveryTo) return false;

        return true;
    }

    private static bool MatchesKeyword(LtlLoadSummary s, string keyword) =>
        ContainsCi(s.LoadNumber, keyword)
        || ContainsCi(s.OrderNumber, keyword)
        || ContainsCi(s.PoNumber, keyword)
        || ContainsCi(s.CustomerName, keyword)
        || ContainsCi(s.Origin?.Label, keyword)
        || ContainsCi(s.Destination?.Label, keyword);

    private static IEnumerable<LtlLoadSummary> ApplySort(IEnumerable<LtlLoadSummary> items, LtlSearchQuery q)
    {
        Func<LtlLoadSummary, IComparable?> key = q.Sort switch
        {
            LtlSortField.DeliveryDate => s => s.ScheduledDeliveryAt,
            LtlSortField.Revenue => s => s.Revenue,
            LtlSortField.RevenuePerMile => s => s.RevenuePerMile,
            LtlSortField.Distance => s => s.Mileage,
            LtlSortField.Weight => s => s.WeightLbs,
            LtlSortField.Customer => s => s.CustomerName,
            LtlSortField.Status => s => s.Status,
            LtlSortField.BillingReadiness => s => s.Billing.IsReadyToBill ? 1 : 0,
            _ => s => s.ScheduledPickupAt,
        };

        // Nulls sort last in both directions so missing data never floats to the top.
        var ordered = q.SortDescending
            ? items.OrderByDescending(s => key(s) is null).ThenByDescending(key)
            : items.OrderBy(s => key(s) is null).ThenBy(key);

        return ordered;
    }

    private static bool EqualsCi(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool ContainsCi(string? value, string token) =>
        value is not null && value.Contains(token.Trim(), StringComparison.OrdinalIgnoreCase);
}
