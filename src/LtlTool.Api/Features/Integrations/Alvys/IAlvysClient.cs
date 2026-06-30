namespace LtlTool.Api.Features.Integrations.Alvys;

/// <summary>
/// Abstraction over the Alvys TMS API for LTL data.
/// <list type="bullet">
///   <item><description><see cref="AlvysClient"/> — live OAuth2 client; the default source of truth.</description></item>
///   <item><description><see cref="FallbackAlvysClient"/> — empty-result stub for local/UAT fallback only.</description></item>
/// </list>
/// </summary>
public interface IAlvysClient
{
    /// <summary>
    /// Searches loads via <c>POST /api/p/v{version}/loads/search</c>.
    /// <paramref name="page"/> is 1-based and translated to the Alvys 0-based page
    /// internally. Convenience overload of <see cref="SearchLoadsAsync(LoadSearchRequest, CancellationToken)"/>.
    /// </summary>
    Task<AlvysLoadsResponse> SearchLoadsAsync(
        int page = 1, int pageSize = 100, string? status = null, CancellationToken ct = default);

    /// <summary>
    /// Searches loads via <c>POST /api/p/v{version}/loads/search</c> with a fully
    /// specified request. <see cref="LoadSearchRequest.Page"/> is the 0-based Alvys page.
    /// </summary>
    Task<AlvysLoadsResponse> SearchLoadsAsync(LoadSearchRequest request, CancellationToken ct = default);

    /// <summary>
    /// Returns a single load by its Alvys load number, or <c>null</c> when not
    /// found or on a transport error.
    /// </summary>
    Task<AlvysLoad?> GetLoadByNumberAsync(string loadNumber, CancellationToken ct = default);

    /// <summary>
    /// Returns a single load by id/loadNumber/orderNumber via
    /// <c>GET /api/p/v{version}/loads?…</c> (at least one criterion required). Returns
    /// <c>null</c> when not found (a 404 can also mean an abandoned creation with no trips)
    /// or on a non-success/transport error — degrading gracefully like the other read paths.
    /// </summary>
    Task<AlvysLoad?> GetLoadAsync(LoadLookup lookup, CancellationToken ct = default);

    /// <summary>
    /// Returns a single trip by id/tripNumber via <c>GET /api/p/v{version}/trips?…</c>
    /// (at least one of id/tripNumber required; <c>includeDeleted</c> optional). Returns
    /// <c>null</c> when not found or on a non-success/transport error.
    /// </summary>
    Task<AlvysTrip?> GetTripAsync(TripLookup lookup, CancellationToken ct = default);

    /// <summary>
    /// Lists the polymorphic stops on a trip via
    /// <c>GET /api/p/v{version}/trips/{tripId}/stops</c>. The Alvys response is a bare array
    /// of appointment/delivery_window/waypoint stops (the <c>$type</c> is preserved), so this
    /// returns a list rather than a paged envelope.
    /// </summary>
    Task<IReadOnlyList<AlvysTripStopDetail>> ListTripStopsAsync(string tripId, CancellationToken ct = default);

    /// <summary>
    /// Lists documents attached to a load via <c>GET /api/p/v{version}/loads/{loadNumber}/documents</c>.
    /// The Alvys response is a bare array, so this returns a list rather than a paged
    /// envelope. Read-only: the time-limited <see cref="AlvysLoadDocument.DownloadUrl"/> is
    /// returned as data but documents are not fetched in this slice.
    /// </summary>
    Task<IReadOnlyList<AlvysLoadDocument>> ListLoadDocumentsAsync(string loadNumber, CancellationToken ct = default);

    /// <summary>
    /// Lists notes on a load via <c>GET /api/p/v{version}/loads/{loadNumber}/notes</c>.
    /// The Alvys response is a bare array, so this returns a list rather than a paged envelope.
    /// </summary>
    Task<IReadOnlyList<AlvysLoadNote>> ListLoadNotesAsync(string loadNumber, CancellationToken ct = default);

    /// <summary>
    /// Searches trips via <c>POST /api/p/v{version}/trips/search</c>.
    /// <see cref="TripSearchRequest.Page"/> is the 0-based Alvys page.
    /// </summary>
    Task<AlvysTripsResponse> SearchTripsAsync(TripSearchRequest request, CancellationToken ct = default);

    /// <summary>
    /// Searches trailers via <c>POST /api/p/v{version}/trailers/search</c> for
    /// equipment master data (capacity/equipment-type/assignment readiness).
    /// <see cref="TrailerSearchRequest.Page"/> is the 0-based Alvys page.
    /// </summary>
    Task<AlvysTrailersResponse> SearchTrailersAsync(TrailerSearchRequest request, CancellationToken ct = default);

    /// <summary>
    /// Searches trucks via <c>POST /api/p/v{version}/trucks/search</c> for equipment
    /// master data (capacity/equipment/assignment readiness).
    /// <see cref="TruckSearchRequest.Page"/> is the 0-based Alvys page.
    /// </summary>
    Task<AlvysTrucksResponse> SearchTrucksAsync(TruckSearchRequest request, CancellationToken ct = default);

    /// <summary>
    /// Searches dispatch preferences via <c>POST /api/p/v{version}/dispatchpreferences/search</c>
    /// for dispatcher/driver/truck/trailer assignment context. The Alvys response is a bare
    /// array, so this returns a list rather than a paged envelope.
    /// </summary>
    Task<IReadOnlyList<AlvysDispatchPreference>> SearchDispatchPreferencesAsync(
        DispatchPreferenceSearchRequest request, CancellationToken ct = default);

    /// <summary>
    /// Searches locations via <c>POST /api/p/v{version}/locations/search</c> for
    /// pickup/delivery/hub/yard geography and shipper/consignee/warehouse context.
    /// <see cref="LocationSearchRequest.Page"/> is the 0-based Alvys page.
    /// </summary>
    Task<AlvysLocationsResponse> SearchLocationsAsync(LocationSearchRequest request, CancellationToken ct = default);

    /// <summary>
    /// Searches drivers via <c>POST /api/p/v{version}/drivers/search</c> for driver
    /// assignment/readiness context. <see cref="DriverSearchRequest.Page"/> is the 0-based Alvys page.
    /// </summary>
    Task<AlvysDriversResponse> SearchDriversAsync(DriverSearchRequest request, CancellationToken ct = default);

    /// <summary>
    /// Searches customers via <c>POST /api/p/v{version}/customers/search</c> for billing
    /// separation, customer policy/approval and customer-specific matching context.
    /// <see cref="CustomerSearchRequest.Page"/> is the 0-based Alvys page.
    /// </summary>
    Task<AlvysCustomersResponse> SearchCustomersAsync(CustomerSearchRequest request, CancellationToken ct = default);

    /// <summary>
    /// Searches users via <c>POST /api/p/v{version}/users/search</c> for dispatcher display
    /// names/roles/filters. <see cref="UserSearchRequest.Page"/> is the 0-based Alvys page.
    /// </summary>
    Task<AlvysUsersResponse> SearchUsersAsync(UserSearchRequest request, CancellationToken ct = default);

    /// <summary>
    /// Searches tenders via <c>POST /api/p/v{version}/tenders/search</c> for inbound EDI/
    /// tender offers used as a planning source. <see cref="TenderSearchRequest.Page"/> is the
    /// 0-based Alvys page.
    /// </summary>
    Task<AlvysTendersResponse> SearchTendersAsync(TenderSearchRequest request, CancellationToken ct = default);

    /// <summary>
    /// Returns a single tender by its Alvys tender id via
    /// <c>GET /api/p/v{version}/tenders/{tenderId}</c>, or <c>null</c> when not found
    /// (404) or on a transport/non-success error — degrading gracefully like the other
    /// read paths.
    /// </summary>
    Task<AlvysTender?> GetTenderByIdAsync(string tenderId, CancellationToken ct = default);
}
