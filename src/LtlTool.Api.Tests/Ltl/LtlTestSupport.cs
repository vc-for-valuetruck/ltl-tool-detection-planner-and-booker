using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using LtlTool.Api.Features.Ltl.Consolidation;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>A <see cref="TimeProvider"/> pinned to a fixed instant for deterministic tests.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>Shared option/clock builders so the LTL service tests stay terse.</summary>
internal static class LtlTestFactory
{
    public static readonly DateTimeOffset Now = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

    public static IOptions<LtlOptions> Options(LtlOptions? options = null) =>
        Microsoft.Extensions.Options.Options.Create(options ?? new LtlOptions());

    public static TimeProvider Clock() => new FixedTimeProvider(Now);

    public static BillingReadinessService Billing(LtlOptions? options = null) =>
        new(Options(options), Clock());

    public static WorkflowStageService Workflow() => new();

    public static LtlNormalizationService Normalizer(LtlOptions? options = null) =>
        new(Options(options), Billing(options), Workflow(), Clock());

    public static MatchScoringService Scorer(LtlOptions? options = null) =>
        new(Options(options), Clock());

    public static VisibilityAnalyzer Visibility() => new();

    public static AccessorialSignalAnalyzer AccessorialAnalyzer() => new();

    public static AccessorialReviewAnalyzer AccessorialReview(LtlOptions? options = null) =>
        new(Options(options));

    /// <summary>
    /// Test-double policy reader that resolves purely from the given
    /// <see cref="ConsolidationOptions.CustomerPolicies"/>. No Alvys calls. Same semantics
    /// as the old inline ResolveTier: name lookup, Unknown default.
    /// </summary>
    public static ICustomerLtlPolicyReader StaticPolicyReader(ConsolidationOptions options) =>
        new StaticLtlPolicyReader(options);

    public static EquipmentEventAnalyzer EquipmentEvents(LtlOptions? options = null) =>
        new(Options(options));

    /// <summary>
    /// Builds a <see cref="MatchService"/> over a fake Alvys client with a fresh in-memory event
    /// cache and (by default) the honest Null prediction provider — so ranking falls back to the
    /// deterministic factor scorer, clearly labeled.
    /// </summary>
    public static MatchService Matcher(
        IAlvysClient alvys,
        IAlvysDriverPredictionProvider? prediction = null,
        LtlOptions? options = null) =>
        new(
            alvys,
            Scorer(options),
            EquipmentEvents(options),
            new WindowFeasibilityAnalyzer(),
            new EquipmentEventCache(new MemoryCache(new MemoryCacheOptions())),
            prediction ?? new NullAlvysDriverPredictionProvider(),
            Options(options));
}

/// <summary>
/// Test-double policy reader that resolves tiers purely from a
/// <see cref="ConsolidationOptions.CustomerPolicies"/> list — no Alvys calls, no cache.
/// Same semantics as the inline ResolveTier the two consolidation services used before
/// <c>ICustomerLtlPolicyReader</c> was introduced.
/// </summary>
internal sealed class StaticLtlPolicyReader(ConsolidationOptions options) : ICustomerLtlPolicyReader
{
    private readonly ConsolidationOptions _opts = options;

    public Task<CustomerPolicyResolution> ResolveAsync(
        string? customerId,
        string? customerName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(customerName))
            return Task.FromResult(CustomerPolicyResolution.Unknown);

        var policy = _opts.CustomerPolicies.FirstOrDefault(
            p => string.Equals(p.Customer, customerName, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(policy is null
            ? CustomerPolicyResolution.Unknown
            : new CustomerPolicyResolution(policy.Tier, CustomerPolicySource.DefaultPolicy));
    }
}

/// <summary>
/// Configurable, in-memory <see cref="IAlvysClient"/> for LTL service/controller tests. Only the
/// read paths exercised by the LTL layer are scripted; the rest throw if unexpectedly called.
/// </summary>
internal class FakeAlvysClient : IAlvysClient
{
    public List<AlvysLoad> Loads { get; set; } = [];
    public AlvysLoad? LoadDetail { get; set; }
    public List<AlvysLoadDocument> Documents { get; set; } = [];

    /// <summary>Document id → scripted downloaded bytes, served by <see cref="DownloadLoadDocumentAsync"/>.</summary>
    public Dictionary<string, AlvysDocumentContent> DownloadableDocuments { get; set; } = [];
    public List<AlvysLoadNote> Notes { get; set; } = [];
    public List<AlvysDriver> Drivers { get; set; } = [];
    public List<AlvysTruck> Trucks { get; set; } = [];
    public List<AlvysTrailerEquipment> Trailers { get; set; } = [];
    public List<AlvysDispatchPreference> DispatchPreferences { get; set; } = [];
    public List<AlvysInvoice> Invoices { get; set; } = [];
    public List<AlvysTrip> Trips { get; set; } = [];
    public List<AlvysVisibilityHistoryEvent> InboundVisibility { get; set; } = [];
    public List<AlvysVisibilityHistoryEvent> OutboundVisibility { get; set; } = [];
    public List<AlvysTruckEvent> TruckEvents { get; set; } = [];
    public List<AlvysTrailerEvent> TrailerEvents { get; set; } = [];
    public List<AlvysCustomer> Customers { get; set; } = [];
    public List<AlvysTender> Tenders { get; set; } = [];
    public List<AlvysLocation> Locations { get; set; } = [];
    public List<AlvysUser> Users { get; set; } = [];

    /// <summary>Trip id → its stops, served by <see cref="ListTripStopsAsync"/>.</summary>
    public Dictionary<string, List<AlvysTripStopDetail>> TripStops { get; set; } = [];

    public int SearchCustomersCallCount { get; private set; }
    public int ListTripStopsCallCount { get; private set; }
    public int SearchTripsCallCount { get; private set; }
    public int SearchTruckEventsCallCount { get; private set; }
    public int SearchTrailerEventsCallCount { get; private set; }

    public Task<AlvysLoadsResponse> SearchLoadsAsync(
        int page = 1, int pageSize = 100, string? status = null, CancellationToken ct = default)
        => throw new NotSupportedException();

    public virtual Task<AlvysLoadsResponse> SearchLoadsAsync(LoadSearchRequest request, CancellationToken ct = default)
    {
        // Honor paging so the sweep terminates: serve one page from the configured list.
        var items = Loads.Skip(request.Page * request.PageSize).Take(request.PageSize).ToList();
        return Task.FromResult(new AlvysLoadsResponse
        {
            Page = request.Page,
            PageSize = request.PageSize,
            Total = Loads.Count,
            Items = items,
        });
    }

    public virtual Task<AlvysLoad?> GetLoadByNumberAsync(string loadNumber, CancellationToken ct = default)
        => Task.FromResult(LoadDetail);

    public virtual Task<AlvysLoad?> GetLoadAsync(LoadLookup lookup, CancellationToken ct = default)
        => Task.FromResult(LoadDetail);

    public Task<IReadOnlyList<AlvysLoadDocument>> ListLoadDocumentsAsync(string loadNumber, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AlvysLoadDocument>>(Documents);

    public Task<AlvysDocumentContent?> DownloadLoadDocumentAsync(
        string loadNumber, string documentId, CancellationToken ct = default)
        => Task.FromResult(DownloadableDocuments.TryGetValue(documentId, out var content) ? content : null);

    public Task<IReadOnlyList<AlvysLoadNote>> ListLoadNotesAsync(string loadNumber, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AlvysLoadNote>>(Notes);

    public Task<AlvysDriversResponse> SearchDriversAsync(DriverSearchRequest request, CancellationToken ct = default)
        => Task.FromResult(new AlvysDriversResponse { Total = Drivers.Count, Items = Drivers });

    public Task<AlvysTrucksResponse> SearchTrucksAsync(TruckSearchRequest request, CancellationToken ct = default)
        => Task.FromResult(new AlvysTrucksResponse { Total = Trucks.Count, Items = Trucks });

    public Task<AlvysTrailersResponse> SearchTrailersAsync(TrailerSearchRequest request, CancellationToken ct = default)
        => Task.FromResult(new AlvysTrailersResponse { Total = Trailers.Count, Items = Trailers });

    public Task<IReadOnlyList<AlvysDispatchPreference>> SearchDispatchPreferencesAsync(
        DispatchPreferenceSearchRequest request, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AlvysDispatchPreference>>(DispatchPreferences);

    public Task<AlvysTrip?> GetTripAsync(TripLookup lookup, CancellationToken ct = default)
        => Task.FromResult(Trips.FirstOrDefault(t => string.Equals(t.Id, lookup.Id, StringComparison.OrdinalIgnoreCase)));
    public Task<IReadOnlyList<AlvysTripStopDetail>> ListTripStopsAsync(string tripId, CancellationToken ct = default)
    {
        ListTripStopsCallCount++;
        var stops = TripStops.TryGetValue(tripId, out var found) ? found : [];
        return Task.FromResult<IReadOnlyList<AlvysTripStopDetail>>(stops);
    }
    public Task<AlvysTripsResponse> SearchTripsAsync(TripSearchRequest request, CancellationToken ct = default)
    {
        SearchTripsCallCount++;
        return Task.FromResult(new AlvysTripsResponse { Total = Trips.Count, Items = Trips });
    }
    public Task<AlvysLocationsResponse> SearchLocationsAsync(LocationSearchRequest request, CancellationToken ct = default)
    {
        // Honor the LocationIds filter so the dispatch-planner enrichment tests are deterministic.
        IEnumerable<AlvysLocation> matched = Locations;
        if (request.LocationIds is { Count: > 0 } ids)
            matched = matched.Where(l => ids.Contains(l.Id, StringComparer.OrdinalIgnoreCase));
        var items = matched.ToList();
        return Task.FromResult(new AlvysLocationsResponse
        {
            Page = request.Page,
            PageSize = request.PageSize,
            Total = items.Count,
            Items = items,
        });
    }

    // Unused read paths in the LTL layer.
    public Task<AlvysCustomersResponse> SearchCustomersAsync(CustomerSearchRequest request, CancellationToken ct = default)
    {
        SearchCustomersCallCount++;
        var items = Customers.Skip(request.Page * request.PageSize).Take(request.PageSize).ToList();
        return Task.FromResult(new AlvysCustomersResponse
        {
            Page = request.Page,
            PageSize = request.PageSize,
            Total = Customers.Count,
            Items = items,
        });
    }
    public Task<AlvysUsersResponse> SearchUsersAsync(UserSearchRequest request, CancellationToken ct = default)
        => Task.FromResult(new AlvysUsersResponse { Total = Users.Count, Items = Users });
    public Task<AlvysTendersResponse> SearchTendersAsync(TenderSearchRequest request, CancellationToken ct = default)
    {
        IEnumerable<AlvysTender> matched = Tenders;
        var loadNumber = request.Filter?.LoadNumber;
        var shipmentId = request.Filter?.ShipmentId;
        if (!string.IsNullOrWhiteSpace(loadNumber))
            matched = matched.Where(t => string.Equals(t.LoadNumber, loadNumber, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(shipmentId))
            matched = matched.Where(t => string.Equals(t.ShipmentId, shipmentId, StringComparison.OrdinalIgnoreCase));

        var items = matched.ToList();
        return Task.FromResult(new AlvysTendersResponse
        {
            Page = request.Page,
            PageSize = request.PageSize,
            Total = items.Count,
            Items = items,
        });
    }
    public Task<AlvysTender?> GetTenderByIdAsync(string tenderId, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<AlvysInvoicesResponse> SearchInvoicesAsync(InvoiceSearchRequest request, CancellationToken ct = default)
        => Task.FromResult(new AlvysInvoicesResponse { Total = Invoices.Count, Items = Invoices });
    public Task<AlvysInvoice?> GetInvoiceAsync(InvoiceLookup lookup, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<IReadOnlyList<AlvysVisibilityHistoryEvent>> ListInboundVisibilityHistoryAsync(string loadNumber, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AlvysVisibilityHistoryEvent>>(InboundVisibility);
    public Task<IReadOnlyList<AlvysVisibilityHistoryEvent>> ListOutboundVisibilityHistoryAsync(string loadNumber, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AlvysVisibilityHistoryEvent>>(OutboundVisibility);
    public Task<IReadOnlyList<AlvysTruckEvent>> SearchTruckEventsAsync(TruckEventSearchRequest request, CancellationToken ct = default)
    {
        SearchTruckEventsCallCount++;
        return Task.FromResult<IReadOnlyList<AlvysTruckEvent>>(TruckEvents);
    }
    public Task<IReadOnlyList<AlvysTrailerEvent>> SearchTrailerEventsAsync(TrailerEventSearchRequest request, CancellationToken ct = default)
    {
        SearchTrailerEventsCallCount++;
        return Task.FromResult<IReadOnlyList<AlvysTrailerEvent>>(TrailerEvents);
    }
}
