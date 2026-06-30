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
}
