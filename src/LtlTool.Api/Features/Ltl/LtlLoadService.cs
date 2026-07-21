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
    IAlvysClient alvys, LtlNormalizationService normalizer, VisibilityAnalyzer visibility,
    AccessorialSignalAnalyzer accessorialAnalyzer, IAccessorialSignalExtractor accessorialExtractor,
    IOptions<LtlOptions> options, TimeProvider clock,
    TenderEnrichmentService? tenderEnrichment = null)
{
    private readonly LtlOptions _options = options.Value;

    /// <summary>Normalized, filtered, sorted, paged LTL search.</summary>
    public async Task<LtlSearchResponse> SearchAsync(LtlSearchQuery query, CancellationToken ct)
    {
        var (loads, truncated) = await SweepAsync(BuildUpstreamRequest(query), ct);

        // Phase 7.2: one tender sweep enriches the whole page with EDI pallet/piece/weight/volume
        // where a tender shares an identifier; null-safe so the search still runs with no tenders.
        var tenderIndex = tenderEnrichment is null ? null : await tenderEnrichment.BuildIndexAsync(ct);
        var normalized = loads
            .Select(l => normalizer.Normalize(
                l, ediEnrichment: tenderIndex is null ? null : tenderEnrichment!.Enrich(l, tenderIndex)))
            .ToList();
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
        var tripEcon = await FetchTripEconomicsForLoadAsync(loadNumber, ct);
        var (context, visibilityExceptions) = await FetchVisibilityAsync(loadNumber, ct);
        var accessorialContext = await BuildAccessorialContextAsync(loadNumber, documents, ct);
        var ediEnrichment = tenderEnrichment is null ? null : await tenderEnrichment.EnrichOneAsync(load, ct);
        return normalizer.Normalize(
            load, documents, invoices, context, visibilityExceptions,
            carrierPayable: tripEcon.CarrierPayable,
            driverTripRate: tripEcon.DriverTripRate,
            loadedMiles: tripEcon.LoadedMiles,
            ediEnrichment: ediEnrichment,
            accessorialSignals: accessorialContext);
    }

    /// <summary>
    /// The three trip-derived economic fields the Consolidation Planner + Billing Worklist care
    /// about: carrier total payable (billing), driver trip rate (dispatch), and loaded miles
    /// (dispatch). All three come from the same <c>SearchTripsAsync</c> call, so this bundle
    /// prevents three round-trips when only one is needed. Missing values remain null; nothing
    /// is inferred.
    /// </summary>
    private readonly record struct TripEconomics(
        decimal? CarrierPayable,
        decimal? DriverTripRate,
        decimal? LoadedMiles);

    /// <summary>
    /// Fetches the inbound + outbound visibility history for a single load (read-only) and turns it
    /// into a detail-timeline context plus any failed-share exception flags. Degrades to a
    /// not-evaluated context with no exceptions when there is no load number to look up.
    /// </summary>
    private async Task<(VisibilityContext Context, IReadOnlyList<LtlExceptionFlag> Exceptions)>
        FetchVisibilityAsync(string loadNumber, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(loadNumber))
            return (VisibilityContext.NotEvaluated, []);

        var inboundTask = alvys.ListInboundVisibilityHistoryAsync(loadNumber, ct);
        var outboundTask = alvys.ListOutboundVisibilityHistoryAsync(loadNumber, ct);
        await Task.WhenAll(inboundTask, outboundTask);

        var inbound = await inboundTask;
        var outbound = await outboundTask;

        var context = visibility.BuildContext(inbound, outbound);
        var exceptions = visibility.DeriveExceptions(loadNumber, inbound, outbound);
        return (context, exceptions);
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

    /// <summary>
    /// Fetches the three trip-derived economic fields for a single load's trip (read-only):
    /// carrier total payable, driver trip rate, loaded miles. All three come from the same
    /// <c>SearchTripsAsync</c> call — one network hop, three signals. Returns
    /// <c>default(TripEconomics)</c> (all-nulls) when there is no trip or the upstream
    /// degrades. Never fabricates values.
    /// </summary>
    private async Task<TripEconomics> FetchTripEconomicsForLoadAsync(string loadNumber, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(loadNumber)) return default;

        var response = await alvys.SearchTripsAsync(
            new TripSearchRequest { Page = 0, PageSize = 5, LoadNumbers = [loadNumber] }, ct);

        // Filter to trips actually matching this load number — the SearchTripsAsync backend is
        // supposed to do that, but some code paths (test fakes, tolerant client-side filters)
        // may return the full set. Never attribute rate on trip A to miles on trip B by taking
        // FirstOrDefault blind.
        var trip = response.Items
            .Where(t =>
                string.Equals(t.LoadNumber, loadNumber, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(t =>
                t.Carrier?.TotalPayable?.Amount is not null
                || t.TripValue?.Amount is not null
                || t.LoadedMileage?.Value is not null);

        if (trip is null) return default;

        return new TripEconomics(
            CarrierPayable: trip.Carrier?.TotalPayable?.Amount,
            DriverTripRate: trip.TripValue?.Amount,
            LoadedMiles: trip.LoadedMileage?.Value);
    }

    /// <summary>
    /// Bulk-fetches carrier total-payable amounts for a set of loads in one paged trip search
    /// keyed by load number, returning a load-number → <see cref="TripEconomics"/> map with
    /// carrier payable, driver trip rate, and loaded miles. Same shape as
    /// <see cref="FetchInvoicesForLoadsAsync"/> so the worklist gets margin context without an
    /// N-call fan-out. Returns an empty map when there are no load numbers or upstream degrades.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, TripEconomics>> FetchTripEconomicsForLoadsAsync(
        IReadOnlyList<AlvysLoad> loads, CancellationToken ct)
    {
        var trips = await SweepTripsForLoadsAsync(loads, ct);
        return BuildEconomicsMap(trips);
    }

    /// <summary>
    /// Bulk-fetches the trips for a set of loads in one paged, load-number-keyed trip search,
    /// returning the raw trips (deduped to the first trip per load number). Shared by the economics
    /// and exception paths so a single sweep can feed both — no double round-trip. Read-only.
    /// </summary>
    private async Task<List<AlvysTrip>> SweepTripsForLoadsAsync(
        IReadOnlyList<AlvysLoad> loads, CancellationToken ct)
    {
        var loadNumbers = loads
            .Select(l => l.LoadNumber)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var trips = new List<AlvysTrip>();
        if (loadNumbers.Count == 0) return trips;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pageSize = _options.AlvysPageSize;

        // Alvys caps a single LoadNumbers filter (see LoadSearchRequest.MaxLoadNumbers), so chunk
        // the load set into batches rather than sending one oversized filter the server would
        // reject or silently truncate — otherwise loads past the cap never get a trip, and their
        // trip-stop exceptions (late DELIVERY, stuck-at-stop) never surface.
        foreach (var chunk in loadNumbers.Chunk(LoadSearchRequest.MaxLoadNumbers))
        {
            var page = 0;
            var fetched = 0;

            while (true)
            {
                var response = await alvys.SearchTripsAsync(
                    new TripSearchRequest { Page = page, PageSize = pageSize, LoadNumbers = [.. chunk] }, ct);
                if (response.Items.Count == 0) break;

                foreach (var trip in response.Items)
                {
                    if (string.IsNullOrWhiteSpace(trip.LoadNumber) || !seen.Add(trip.LoadNumber!)) continue;
                    trips.Add(trip);
                }

                fetched += response.Items.Count;
                if (fetched >= response.Total || response.Items.Count < pageSize) break;
                page++;
            }
        }

        return trips;
    }

    /// <summary>Builds the load-number → economics map from a swept trip set (pure).</summary>
    private static IReadOnlyDictionary<string, TripEconomics> BuildEconomicsMap(IReadOnlyList<AlvysTrip> trips)
    {
        var map = new Dictionary<string, TripEconomics>(StringComparer.OrdinalIgnoreCase);
        foreach (var trip in trips)
        {
            if (string.IsNullOrWhiteSpace(trip.LoadNumber) || map.ContainsKey(trip.LoadNumber!)) continue;
            var payable = trip.Carrier?.TotalPayable?.Amount;
            var driverRate = trip.TripValue?.Amount;
            var loadedMiles = trip.LoadedMileage?.Value;
            if (payable is null && driverRate is null && loadedMiles is null) continue;
            map[trip.LoadNumber!] = new TripEconomics(payable, driverRate, loadedMiles);
        }

        return map;
    }

    /// <summary>
    /// Builds the load-number → actual-late-delivery map from a swept trip set. A trip contributes
    /// an entry only when its delivery stop's window/appointment has passed with no recorded arrival
    /// (see <see cref="LateDeliveryDetector"/>). Pure over the already-fetched trips.
    /// </summary>
    private IReadOnlyDictionary<string, LtlLateDelivery> BuildLateDeliveryMap(IReadOnlyList<AlvysTrip> trips)
    {
        var now = clock.GetUtcNow();
        var map = new Dictionary<string, LtlLateDelivery>(StringComparer.OrdinalIgnoreCase);
        foreach (var trip in trips)
        {
            if (string.IsNullOrWhiteSpace(trip.LoadNumber) || map.ContainsKey(trip.LoadNumber!)) continue;
            var late = LateDeliveryDetector.Detect(trip, now, _options.LateDeliveryGraceMinutes);
            if (late is not null) map[trip.LoadNumber!] = late;
        }

        return map;
    }

    private IReadOnlyDictionary<string, LtlStuckStop> BuildStuckStopMap(IReadOnlyList<AlvysTrip> trips)
    {
        var now = clock.GetUtcNow();
        var map = new Dictionary<string, LtlStuckStop>(StringComparer.OrdinalIgnoreCase);
        foreach (var trip in trips)
        {
            if (string.IsNullOrWhiteSpace(trip.LoadNumber) || map.ContainsKey(trip.LoadNumber!)) continue;
            var stuck = StuckAtStopDetector.Detect(trip, now, _options.StuckAtStopThresholdHours);
            if (stuck is not null) map[trip.LoadNumber!] = stuck;
        }

        return map;
    }

    /// <summary>Raw + documents resolution used by detail and matching.</summary>
    public async Task<AlvysLoad?> ResolveLoadAsync(string idOrNumber, CancellationToken ct)
    {
        // Alvys's /loads detail endpoint rejects requests that include more than one of
        // id / loadNumber / orderNumber (HTTP 400: "Only one of 'id', 'loadNumber' or
        // 'orderNumber' should be provided."). Discriminate up front: values shaped like a
        // UUID are treated as an Alvys Id; everything else falls through to loadNumber, which
        // is the shape the LTL UI links use (L-1001805 → "1001805"). Callers that hold an
        // orderNumber can call GetLoadAsync directly with an explicit LoadLookup.
        var lookup = LooksLikeGuid(idOrNumber)
            ? new LoadLookup { Id = idOrNumber }
            : new LoadLookup { LoadNumber = idOrNumber };
        return await alvys.GetLoadAsync(lookup, ct);
    }

    private static bool LooksLikeGuid(string s) => Guid.TryParse(s, out _);

    /// <summary>
    /// Billing worklist: normalized loads that still need billing attention (not yet invoiced),
    /// optionally filtered to a single badge, sorted by readiness then pickup.
    /// </summary>
    public async Task<IReadOnlyList<LtlLoadSummary>> BillingWorklistAsync(
        BillingBadge? badge, CancellationToken ct)
    {
        var (loads, _) = await SweepAsync(new LoadSearchRequest { PageSize = _options.AlvysPageSize }, ct);

        // Bulk-fetch invoices for the whole swept set in one paged search (LoadNumbers is a list
        // filter upstream) rather than one call per load, so invoice-backed billing readiness
        // extends to the worklist without an N-call fan-out.
        var invoicesByLoad = await FetchInvoicesForLoadsAsync(loads, ct);
        var tripEconByLoad = await FetchTripEconomicsForLoadsAsync(loads, ct);

        var normalized = loads.Select(l =>
        {
            var econ = !string.IsNullOrWhiteSpace(l.LoadNumber)
                && tripEconByLoad.TryGetValue(l.LoadNumber!, out var e) ? e : default;
            return normalizer.Normalize(
                l,
                invoices: InvoicesFor(l, invoicesByLoad),
                carrierPayable: econ.CarrierPayable,
                driverTripRate: econ.DriverTripRate,
                loadedMiles: econ.LoadedMiles);
        });

        return normalized
            .Where(s => !s.Billing.IsAlreadyInvoiced)
            .Where(s => badge is null || s.Billing.Badges.Contains(badge.Value))
            .OrderByDescending(s => s.Billing.IsReadyToBill)
            .ThenBy(s => s.ScheduledPickupAt ?? DateTimeOffset.MaxValue)
            .ToList();
    }

    /// <summary>
    /// Loads carrying one or more operational/billing exceptions. The first
    /// <see cref="LtlOptions.MaxVisibilityEnriched"/> scanned loads are additionally enriched with a
    /// per-load visibility-history fetch so failed/errored tracking shares surface here too; loads
    /// past that bound keep their load-derived exceptions only (visibility-only signals still
    /// surface on the detail path). This bound keeps the extra upstream calls fixed.
    /// </summary>
    public async Task<IReadOnlyList<LtlLoadSummary>> ExceptionsAsync(CancellationToken ct)
    {
        // General sweep across all statuses — where billing/status/visibility exceptions live.
        var (generalLoads, _) = await SweepAsync(
            new LoadSearchRequest { PageSize = _options.AlvysPageSize }, ct);

        // The general sweep is bounded and ordered by Alvys recency, so genuinely-late in-transit
        // loads that haven't been touched recently can fall outside its window — their trips never
        // get fetched and the trip-stop detectors (late DELIVERY, stuck-at-stop) never run. Sweep
        // the dispatched-but-not-delivered statuses explicitly and union so that population is
        // reliably scanned. Read-only; both sweeps are individually bounded.
        var (activeLoads, _) = await SweepAsync(
            new LoadSearchRequest
            {
                PageSize = _options.AlvysPageSize,
                Status = LoadSearchRequest.ActiveTransitStatuses,
            }, ct);

        var loads = UnionLoadsByNumber(generalLoads, activeLoads);

        // One trip sweep feeds three exception signals: trip economics (loaded miles → the
        // predicted-delivery ETA, so a predicted-late arrival surfaces before it actually goes late),
        // actual-late DELIVERY detection (delivery-stop window passed with no arrival recorded), and
        // stuck-at-stop detection (arrived with no departure past the dwell threshold).
        // Missing data simply yields no signal — never a guess.
        var trips = await SweepTripsForLoadsAsync(loads, ct);
        var tripEconByLoad = BuildEconomicsMap(trips);
        var lateDeliveryByLoad = BuildLateDeliveryMap(trips);
        var stuckStopByLoad = BuildStuckStopMap(trips);

        var enrichLimit = Math.Max(0, _options.MaxVisibilityEnriched);
        var summaries = new List<LtlLoadSummary>(loads.Count);
        var enriched = 0;

        foreach (var load in loads)
        {
            var loadNumber = load.LoadNumber;
            var econ = !string.IsNullOrWhiteSpace(loadNumber)
                && tripEconByLoad.TryGetValue(loadNumber, out var e)
                ? e
                : default;
            var lateDelivery = !string.IsNullOrWhiteSpace(loadNumber)
                && lateDeliveryByLoad.TryGetValue(loadNumber, out var ld)
                ? ld
                : null;
            var stuckStop = !string.IsNullOrWhiteSpace(loadNumber)
                && stuckStopByLoad.TryGetValue(loadNumber, out var ss)
                ? ss
                : null;

            if (enriched < enrichLimit && !string.IsNullOrWhiteSpace(loadNumber))
            {
                var (context, visibilityExceptions) = await FetchVisibilityAsync(loadNumber, ct);
                summaries.Add(normalizer.Normalize(
                    load, visibility: context, extraExceptions: visibilityExceptions,
                    carrierPayable: econ.CarrierPayable,
                    driverTripRate: econ.DriverTripRate,
                    loadedMiles: econ.LoadedMiles,
                    lateDelivery: lateDelivery,
                    stuckStop: stuckStop));
                enriched++;
            }
            else
            {
                summaries.Add(normalizer.Normalize(
                    load,
                    carrierPayable: econ.CarrierPayable,
                    driverTripRate: econ.DriverTripRate,
                    loadedMiles: econ.LoadedMiles,
                    lateDelivery: lateDelivery,
                    stuckStop: stuckStop));
            }
        }

        return summaries
            .Where(s => s.HasExceptions)
            .OrderByDescending(s => s.Exceptions.Count(e => e.BlocksBilling))
            .ThenByDescending(s => s.Exceptions.Count)
            .ToList();
    }

    /// <summary>
    /// Recent lane rate context (Phase 7.4): the revenue-per-mile spread across recently
    /// <em>Delivered</em> loads on the same origin→destination state pair. This is deliberately
    /// "recent tenant history, not market rate" — it is what Value Truck itself has recently billed
    /// on the lane, read live from Alvys, NOT a DAT/Greenscreens market feed (an explicit non-goal
    /// for this slice). Below <see cref="LtlOptions.LaneRateMinSamples"/> priced samples the lane
    /// reports an honest <see cref="LaneRateContext.Insufficient"/> verdict rather than a thin range.
    /// Read-only; nothing writes back to Alvys.
    /// </summary>
    public async Task<LaneRateContext> LaneRateContextAsync(
        string originState, string destinationState, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        originState = (originState ?? string.Empty).Trim();
        destinationState = (destinationState ?? string.Empty).Trim();

        var (loads, _) = await SweepAsync(
            new LoadSearchRequest { PageSize = _options.AlvysPageSize, Status = ["Delivered"] }, ct);

        var rpms = loads
            .Select(l => normalizer.Normalize(l))
            .Where(s => EqualsCi(s.Origin?.State, originState)
                && EqualsCi(s.Destination?.State, destinationState))
            .Select(s => s.RevenuePerMile)
            .Where(rpm => rpm is > 0)
            .Select(rpm => rpm!.Value)
            .OrderBy(rpm => rpm)
            .ToList();

        if (rpms.Count < Math.Max(1, _options.LaneRateMinSamples))
            return LaneRateContext.Insufficient(originState, destinationState, rpms.Count, now);

        return new LaneRateContext
        {
            OriginState = originState,
            DestinationState = destinationState,
            SampleSize = rpms.Count,
            MedianRpm = Median(rpms),
            MinRpm = rpms[0],
            MaxRpm = rpms[^1],
            Basis =
                $"Median revenue-per-mile across {rpms.Count} recently delivered load(s) on "
                + $"{originState}→{destinationState}. Recent tenant history (what Value Truck billed), "
                + "not a market rate.",
            GeneratedAt = now,
        };
    }

    /// <summary>Median of a pre-sorted, non-empty list.</summary>
    private static decimal Median(IReadOnlyList<decimal> sorted)
    {
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2m;
    }

    /// <summary>
    /// Bulk-fetches invoices for a set of loads in one paged invoice search keyed by load number,
    /// returning a load-number → invoices map. Returns an empty map when there are no load numbers
    /// or the upstream degrades — never fabricates billing state.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, List<AlvysInvoice>>> FetchInvoicesForLoadsAsync(
        IReadOnlyList<AlvysLoad> loads, CancellationToken ct)
    {
        var loadNumbers = loads
            .Select(l => l.LoadNumber)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var map = new Dictionary<string, List<AlvysInvoice>>(StringComparer.OrdinalIgnoreCase);
        if (loadNumbers.Count == 0) return map;

        var page = 0;
        var pageSize = _options.AlvysPageSize;
        var fetched = 0;

        while (true)
        {
            var response = await alvys.SearchInvoicesAsync(
                new InvoiceSearchRequest { Page = page, PageSize = pageSize, LoadNumbers = loadNumbers }, ct);
            if (response.Items.Count == 0) break;

            foreach (var invoice in response.Items)
            {
                foreach (var loadRef in invoice.Loads ?? [])
                {
                    if (string.IsNullOrWhiteSpace(loadRef.LoadNumber)) continue;
                    if (!map.TryGetValue(loadRef.LoadNumber!, out var list))
                        map[loadRef.LoadNumber!] = list = [];
                    list.Add(invoice);
                }
            }

            fetched += response.Items.Count;
            if (fetched >= response.Total || response.Items.Count < pageSize) break;
            if (fetched >= _options.MaxLoadsScanned) break;
            page++;
        }

        return map;
    }

    private static IReadOnlyList<AlvysInvoice>? InvoicesFor(
        AlvysLoad load, IReadOnlyDictionary<string, List<AlvysInvoice>> invoicesByLoad)
    {
        if (string.IsNullOrWhiteSpace(load.LoadNumber)) return null;
        return invoicesByLoad.TryGetValue(load.LoadNumber!, out var list) ? list : [];
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

    /// <summary>
    /// Concatenates two swept load sets, keeping the first occurrence per load number (loads with
    /// no load number are all kept). Order is preserved: the primary set first, then any loads the
    /// secondary set adds. Used to union the general and active-transit exception sweeps.
    /// </summary>
    private static List<AlvysLoad> UnionLoadsByNumber(
        IReadOnlyList<AlvysLoad> primary, IReadOnlyList<AlvysLoad> secondary)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var union = new List<AlvysLoad>(primary.Count + secondary.Count);

        foreach (var load in primary.Concat(secondary))
        {
            if (!string.IsNullOrWhiteSpace(load.LoadNumber) && !seen.Add(load.LoadNumber!))
                continue;
            union.Add(load);
        }

        return union;
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
        if (q.Stage is not null && s.Workflow.Stage != q.Stage.Value) return false;
        if (q.BlockedOnly && !s.Workflow.IsBlocked) return false;

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
            ? items.OrderBy(s => key(s) is null).ThenByDescending(key)
            : items.OrderBy(s => key(s) is null).ThenBy(key);

        return ordered;
    }

    private static bool EqualsCi(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool ContainsCi(string? value, string token) =>
        value is not null && value.Contains(token.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fetches notes and documents for a load from Alvys, runs the deterministic accessorial
    /// keyword extraction, and supplements with AI-derived signals when enabled. Returns
    /// <c>null</c> when the load is not found; returns
    /// <see cref="AccessorialReviewContext.NotEvaluated"/> when the load has neither notes nor
    /// documents (per the "never a false clean" principle — no evidence is not the same as clean).
    ///
    /// <para>Read-only: no data is written back to Alvys.</para>
    /// </summary>
    public async Task<AccessorialReviewContext?> GetAccessorialSignalsAsync(
        string idOrNumber, CancellationToken ct)
    {
        var load = await ResolveLoadAsync(idOrNumber, ct);
        if (load is null) return null;

        var loadNumber = load.LoadNumber ?? idOrNumber;
        var documents = await alvys.ListLoadDocumentsAsync(loadNumber, ct);
        return await BuildAccessorialContextAsync(loadNumber, documents, ct);
    }

    /// <summary>
    /// Fetches notes for a load and builds the accessorial-signal context (deterministic keyword
    /// extraction, plus AI supplement when enabled) against the caller's already-fetched
    /// documents. Shared by <see cref="GetAccessorialSignalsAsync"/> and <see cref="GetDetailAsync"/>
    /// so the detail path does not re-fetch documents just to fold this signal into billing
    /// readiness.
    /// </summary>
    private async Task<AccessorialReviewContext> BuildAccessorialContextAsync(
        string loadNumber, IReadOnlyList<AlvysLoadDocument> documents, CancellationToken ct)
    {
        var notes = await alvys.ListLoadNotesAsync(loadNumber, ct);

        // Deterministic keyword extraction (always runs, synchronous, pure).
        var context = accessorialAnalyzer.BuildContext(notes, documents);

        // AI supplement (disabled by default; degrades to empty on any failure).
        if (accessorialExtractor.IsEnabled && context.Evaluated)
        {
            var aiSignals = new List<AccessorialSignal>();
            foreach (var note in notes)
            {
                if (string.IsNullOrWhiteSpace(note.Description)) continue;
                var signals = await accessorialExtractor.ExtractAsync(note.Id, "Note", note.Description, ct);
                aiSignals.AddRange(signals);
            }
            if (aiSignals.Count > 0)
                context = AccessorialSignalAnalyzer.MergeAiSignals(context, aiSignals);
        }

        return context;
    }
}
