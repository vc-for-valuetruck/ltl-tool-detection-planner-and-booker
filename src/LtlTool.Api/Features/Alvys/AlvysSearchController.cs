using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LtlTool.Api.Features.Alvys;

/// <summary>
/// Server-side read-only proxy over <see cref="IAlvysClient"/> for the dispatcher SPA.
///
/// The Angular app calls these internal endpoints instead of Alvys directly, so Alvys
/// OAuth credentials stay server-side and are never shipped to the browser. Live Alvys
/// remains the default source of truth (see <see cref="AlvysProvider"/>).
///
/// <para>
/// This slice is <b>read-only</b>: every action is a search/query that passes the
/// request straight through to the corresponding <c>IAlvysClient</c> method and returns
/// the paged Alvys read model. No data is created, updated or deleted, and there is no
/// writeback to Alvys. Alvys models searches as <c>POST</c> (the filter set is the
/// request body), so these read endpoints are also <c>POST</c>.
/// </para>
/// </summary>
[ApiController]
[Route("api/alvys")]
[Authorize(Policy = AccessPolicies.AllowedEmailDomain)]
[Produces("application/json")]
public sealed class AlvysSearchController(IAlvysClient alvys) : ControllerBase
{
    /// <summary>
    /// Read-only load (open-freight) search. Passes <paramref name="request"/> through to
    /// <see cref="IAlvysClient.SearchLoadsAsync(LoadSearchRequest, CancellationToken)"/>.
    /// </summary>
    [HttpPost("loads/search")]
    [ProducesResponseType(typeof(AlvysLoadsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AlvysLoadsResponse>> SearchLoads(
        [FromBody] LoadSearchRequest request, CancellationToken ct)
        => Ok(await alvys.SearchLoadsAsync(request, ct));

    /// <summary>
    /// Read-only trip search. Passes <paramref name="request"/> through to
    /// <see cref="IAlvysClient.SearchTripsAsync(TripSearchRequest, CancellationToken)"/>.
    /// </summary>
    [HttpPost("trips/search")]
    [ProducesResponseType(typeof(AlvysTripsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AlvysTripsResponse>> SearchTrips(
        [FromBody] TripSearchRequest request, CancellationToken ct)
        => Ok(await alvys.SearchTripsAsync(request, ct));

    /// <summary>
    /// Read-only trailer equipment search. Passes <paramref name="request"/> through to
    /// <see cref="IAlvysClient.SearchTrailersAsync(TrailerSearchRequest, CancellationToken)"/>.
    /// </summary>
    [HttpPost("trailers/search")]
    [ProducesResponseType(typeof(AlvysTrailersResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AlvysTrailersResponse>> SearchTrailers(
        [FromBody] TrailerSearchRequest request, CancellationToken ct)
        => Ok(await alvys.SearchTrailersAsync(request, ct));

    /// <summary>
    /// Read-only truck equipment search. Passes <paramref name="request"/> through to
    /// <see cref="IAlvysClient.SearchTrucksAsync(TruckSearchRequest, CancellationToken)"/>.
    /// </summary>
    [HttpPost("trucks/search")]
    [ProducesResponseType(typeof(AlvysTrucksResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AlvysTrucksResponse>> SearchTrucks(
        [FromBody] TruckSearchRequest request, CancellationToken ct)
        => Ok(await alvys.SearchTrucksAsync(request, ct));

    /// <summary>
    /// Read-only dispatch-preference search (dispatcher/driver/truck/trailer assignment
    /// context). Passes <paramref name="request"/> through to
    /// <see cref="IAlvysClient.SearchDispatchPreferencesAsync(DispatchPreferenceSearchRequest, CancellationToken)"/>.
    /// Returns a bare array, matching the upstream Alvys shape.
    /// </summary>
    [HttpPost("dispatch-preferences/search")]
    [ProducesResponseType(typeof(IReadOnlyList<AlvysDispatchPreference>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AlvysDispatchPreference>>> SearchDispatchPreferences(
        [FromBody] DispatchPreferenceSearchRequest request, CancellationToken ct)
        => Ok(await alvys.SearchDispatchPreferencesAsync(request, ct));

    /// <summary>
    /// Read-only location search (pickup/delivery/hub/yard geography, shipper/consignee/
    /// warehouse context). Passes <paramref name="request"/> through to
    /// <see cref="IAlvysClient.SearchLocationsAsync(LocationSearchRequest, CancellationToken)"/>.
    /// </summary>
    [HttpPost("locations/search")]
    [ProducesResponseType(typeof(AlvysLocationsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AlvysLocationsResponse>> SearchLocations(
        [FromBody] LocationSearchRequest request, CancellationToken ct)
        => Ok(await alvys.SearchLocationsAsync(request, ct));

    /// <summary>
    /// Read-only driver search (assignment/readiness context). Passes <paramref name="request"/>
    /// through to <see cref="IAlvysClient.SearchDriversAsync(DriverSearchRequest, CancellationToken)"/>.
    /// </summary>
    [HttpPost("drivers/search")]
    [ProducesResponseType(typeof(AlvysDriversResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AlvysDriversResponse>> SearchDrivers(
        [FromBody] DriverSearchRequest request, CancellationToken ct)
        => Ok(await alvys.SearchDriversAsync(request, ct));

    /// <summary>
    /// Read-only customer search (billing separation, customer policy/approval, customer-
    /// specific matching context). Passes <paramref name="request"/> through to
    /// <see cref="IAlvysClient.SearchCustomersAsync(CustomerSearchRequest, CancellationToken)"/>.
    /// </summary>
    [HttpPost("customers/search")]
    [ProducesResponseType(typeof(AlvysCustomersResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AlvysCustomersResponse>> SearchCustomers(
        [FromBody] CustomerSearchRequest request, CancellationToken ct)
        => Ok(await alvys.SearchCustomersAsync(request, ct));

    /// <summary>
    /// Read-only user search (dispatcher display names/roles/filters). Passes
    /// <paramref name="request"/> through to
    /// <see cref="IAlvysClient.SearchUsersAsync(UserSearchRequest, CancellationToken)"/>.
    /// </summary>
    [HttpPost("users/search")]
    [ProducesResponseType(typeof(AlvysUsersResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AlvysUsersResponse>> SearchUsers(
        [FromBody] UserSearchRequest request, CancellationToken ct)
        => Ok(await alvys.SearchUsersAsync(request, ct));

    /// <summary>
    /// Read-only tender (inbound EDI offer) search. Passes <paramref name="request"/> through
    /// to <see cref="IAlvysClient.SearchTendersAsync(TenderSearchRequest, CancellationToken)"/>.
    /// </summary>
    [HttpPost("tenders/search")]
    [ProducesResponseType(typeof(AlvysTendersResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AlvysTendersResponse>> SearchTenders(
        [FromBody] TenderSearchRequest request, CancellationToken ct)
        => Ok(await alvys.SearchTendersAsync(request, ct));

    /// <summary>
    /// Read-only single-tender lookup by Alvys tender id. Returns 404 when the tender is not
    /// found (or upstream degraded to <c>null</c>), mirroring the graceful-degradation stance
    /// of the other read paths.
    /// </summary>
    [HttpGet("tenders/{tenderId}")]
    [ProducesResponseType(typeof(AlvysTender), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AlvysTender>> GetTender(string tenderId, CancellationToken ct)
    {
        var tender = await alvys.GetTenderByIdAsync(tenderId, ct);
        return tender is null ? NotFound() : Ok(tender);
    }
}
