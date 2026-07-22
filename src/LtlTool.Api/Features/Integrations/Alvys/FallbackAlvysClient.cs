namespace LtlTool.Api.Features.Integrations.Alvys;

/// <summary>
/// NON-DEFAULT fallback client for local development and UAT only.
///
/// Returns empty results so the app boots and pages render without a live Alvys
/// tenant. It is never the source of truth and must not be selected in
/// production-like configuration — activate it explicitly via
/// <c>Alvys:Provider = Fallback</c>. Empty results preserve the live response
/// shape (paged envelopes) so callers behave identically.
/// </summary>
public sealed class FallbackAlvysClient : IAlvysClient
{
    public Task<AlvysLoadsResponse> SearchLoadsAsync(
        int page = 1, int pageSize = 100, string? status = null, CancellationToken ct = default)
        => Task.FromResult(new AlvysLoadsResponse());

    public Task<AlvysLoadsResponse> SearchLoadsAsync(
        LoadSearchRequest request, CancellationToken ct = default)
        => Task.FromResult(new AlvysLoadsResponse());

    public Task<AlvysLoad?> GetLoadByNumberAsync(string loadNumber, CancellationToken ct = default)
        => Task.FromResult<AlvysLoad?>(null);

    public Task<AlvysLoad?> GetLoadAsync(LoadLookup lookup, CancellationToken ct = default)
        => Task.FromResult<AlvysLoad?>(null);

    public Task<AlvysTrip?> GetTripAsync(TripLookup lookup, CancellationToken ct = default)
        => Task.FromResult<AlvysTrip?>(null);

    public Task<IReadOnlyList<AlvysTripStopDetail>> ListTripStopsAsync(
        string tripId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AlvysTripStopDetail>>([]);

    public Task<IReadOnlyList<AlvysLoadDocument>> ListLoadDocumentsAsync(
        string loadNumber, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AlvysLoadDocument>>([]);

    public Task<AlvysDocumentContent?> DownloadLoadDocumentAsync(
        string loadNumber, string documentId, CancellationToken ct = default)
        => Task.FromResult<AlvysDocumentContent?>(null);

    public Task<IReadOnlyList<AlvysLoadNote>> ListLoadNotesAsync(
        string loadNumber, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AlvysLoadNote>>([]);

    public Task<AlvysTripsResponse> SearchTripsAsync(
        TripSearchRequest request, CancellationToken ct = default)
        => Task.FromResult(new AlvysTripsResponse());

    public Task<AlvysTrailersResponse> SearchTrailersAsync(
        TrailerSearchRequest request, CancellationToken ct = default)
        => Task.FromResult(new AlvysTrailersResponse());

    public Task<AlvysTrucksResponse> SearchTrucksAsync(
        TruckSearchRequest request, CancellationToken ct = default)
        => Task.FromResult(new AlvysTrucksResponse());

    public Task<IReadOnlyList<AlvysDispatchPreference>> SearchDispatchPreferencesAsync(
        DispatchPreferenceSearchRequest request, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AlvysDispatchPreference>>([]);

    public Task<AlvysLocationsResponse> SearchLocationsAsync(
        LocationSearchRequest request, CancellationToken ct = default)
        => Task.FromResult(new AlvysLocationsResponse());

    public Task<AlvysDriversResponse> SearchDriversAsync(
        DriverSearchRequest request, CancellationToken ct = default)
        => Task.FromResult(new AlvysDriversResponse());

    public Task<AlvysCustomersResponse> SearchCustomersAsync(
        CustomerSearchRequest request, CancellationToken ct = default)
        => Task.FromResult(new AlvysCustomersResponse());

    public Task<AlvysUsersResponse> SearchUsersAsync(
        UserSearchRequest request, CancellationToken ct = default)
        => Task.FromResult(new AlvysUsersResponse());

    public Task<AlvysTendersResponse> SearchTendersAsync(
        TenderSearchRequest request, CancellationToken ct = default)
        => Task.FromResult(new AlvysTendersResponse());

    public Task<AlvysTender?> GetTenderByIdAsync(string tenderId, CancellationToken ct = default)
        => Task.FromResult<AlvysTender?>(null);

    public Task<AlvysInvoicesResponse> SearchInvoicesAsync(
        InvoiceSearchRequest request, CancellationToken ct = default)
        => Task.FromResult(new AlvysInvoicesResponse());

    public Task<AlvysInvoice?> GetInvoiceAsync(InvoiceLookup lookup, CancellationToken ct = default)
        => Task.FromResult<AlvysInvoice?>(null);

    public Task<IReadOnlyList<AlvysVisibilityHistoryEvent>> ListInboundVisibilityHistoryAsync(
        string loadNumber, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AlvysVisibilityHistoryEvent>>([]);

    public Task<IReadOnlyList<AlvysVisibilityHistoryEvent>> ListOutboundVisibilityHistoryAsync(
        string loadNumber, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AlvysVisibilityHistoryEvent>>([]);

    public Task<IReadOnlyList<AlvysTruckEvent>> SearchTruckEventsAsync(
        TruckEventSearchRequest request, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AlvysTruckEvent>>([]);

    public Task<IReadOnlyList<AlvysTrailerEvent>> SearchTrailerEventsAsync(
        TrailerEventSearchRequest request, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AlvysTrailerEvent>>([]);
}
