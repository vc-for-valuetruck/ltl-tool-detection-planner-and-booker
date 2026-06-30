using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
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

    public static LtlNormalizationService Normalizer(LtlOptions? options = null) =>
        new(Options(options), Billing(options));

    public static MatchScoringService Scorer(LtlOptions? options = null) =>
        new(Options(options), Clock());
}

/// <summary>
/// Configurable, in-memory <see cref="IAlvysClient"/> for LTL service/controller tests. Only the
/// read paths exercised by the LTL layer are scripted; the rest throw if unexpectedly called.
/// </summary>
internal sealed class FakeAlvysClient : IAlvysClient
{
    public List<AlvysLoad> Loads { get; set; } = [];
    public AlvysLoad? LoadDetail { get; set; }
    public List<AlvysLoadDocument> Documents { get; set; } = [];
    public List<AlvysDriver> Drivers { get; set; } = [];
    public List<AlvysTruck> Trucks { get; set; } = [];
    public List<AlvysTrailerEquipment> Trailers { get; set; } = [];
    public List<AlvysDispatchPreference> DispatchPreferences { get; set; } = [];
    public List<AlvysInvoice> Invoices { get; set; } = [];

    public Task<AlvysLoadsResponse> SearchLoadsAsync(
        int page = 1, int pageSize = 100, string? status = null, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<AlvysLoadsResponse> SearchLoadsAsync(LoadSearchRequest request, CancellationToken ct = default)
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

    public Task<AlvysLoad?> GetLoadByNumberAsync(string loadNumber, CancellationToken ct = default)
        => Task.FromResult(LoadDetail);

    public Task<AlvysLoad?> GetLoadAsync(LoadLookup lookup, CancellationToken ct = default)
        => Task.FromResult(LoadDetail);

    public Task<IReadOnlyList<AlvysLoadDocument>> ListLoadDocumentsAsync(string loadNumber, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AlvysLoadDocument>>(Documents);

    public Task<AlvysDriversResponse> SearchDriversAsync(DriverSearchRequest request, CancellationToken ct = default)
        => Task.FromResult(new AlvysDriversResponse { Total = Drivers.Count, Items = Drivers });

    public Task<AlvysTrucksResponse> SearchTrucksAsync(TruckSearchRequest request, CancellationToken ct = default)
        => Task.FromResult(new AlvysTrucksResponse { Total = Trucks.Count, Items = Trucks });

    public Task<AlvysTrailersResponse> SearchTrailersAsync(TrailerSearchRequest request, CancellationToken ct = default)
        => Task.FromResult(new AlvysTrailersResponse { Total = Trailers.Count, Items = Trailers });

    public Task<IReadOnlyList<AlvysDispatchPreference>> SearchDispatchPreferencesAsync(
        DispatchPreferenceSearchRequest request, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AlvysDispatchPreference>>(DispatchPreferences);

    // Unused read paths in the LTL layer.
    public Task<AlvysTrip?> GetTripAsync(TripLookup lookup, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<IReadOnlyList<AlvysTripStopDetail>> ListTripStopsAsync(string tripId, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<IReadOnlyList<AlvysLoadNote>> ListLoadNotesAsync(string loadNumber, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<AlvysTripsResponse> SearchTripsAsync(TripSearchRequest request, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<AlvysLocationsResponse> SearchLocationsAsync(LocationSearchRequest request, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<AlvysCustomersResponse> SearchCustomersAsync(CustomerSearchRequest request, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<AlvysUsersResponse> SearchUsersAsync(UserSearchRequest request, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<AlvysTendersResponse> SearchTendersAsync(TenderSearchRequest request, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<AlvysTender?> GetTenderByIdAsync(string tenderId, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<AlvysInvoicesResponse> SearchInvoicesAsync(InvoiceSearchRequest request, CancellationToken ct = default)
        => Task.FromResult(new AlvysInvoicesResponse { Total = Invoices.Count, Items = Invoices });
    public Task<AlvysInvoice?> GetInvoiceAsync(InvoiceLookup lookup, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<IReadOnlyList<AlvysVisibilityHistoryEvent>> ListInboundVisibilityHistoryAsync(string loadNumber, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<IReadOnlyList<AlvysVisibilityHistoryEvent>> ListOutboundVisibilityHistoryAsync(string loadNumber, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<IReadOnlyList<AlvysTruckEvent>> SearchTruckEventsAsync(TruckEventSearchRequest request, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<IReadOnlyList<AlvysTrailerEvent>> SearchTrailerEventsAsync(TrailerEventSearchRequest request, CancellationToken ct = default)
        => throw new NotSupportedException();
}
