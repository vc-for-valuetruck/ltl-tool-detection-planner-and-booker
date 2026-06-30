using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Integrations.Alvys;

/// <summary>
/// Live Alvys TMS client and the default source of truth for LTL data.
///
/// Uses a named <see cref="HttpClient"/> (host base address + timeout configured at
/// registration) and attaches a bearer token from <see cref="IAlvysTokenProvider"/>
/// per request. The versioned <c>/api/p/v{version}/...</c> path is built per request
/// from <see cref="AlvysOptions.ApiVersion"/>. Non-success responses are logged
/// (status only — never bodies, which may echo secrets) and surfaced as empty
/// results / null so callers degrade gracefully.
/// </summary>
public sealed class AlvysClient(
    IHttpClientFactory httpClientFactory,
    IAlvysTokenProvider tokenProvider,
    IOptions<AlvysOptions> options,
    ILogger<AlvysClient> logger) : IAlvysClient
{
    /// <summary>Named client used for Alvys API (non-auth) calls.</summary>
    public const string ApiHttpClientName = "AlvysApi";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    private readonly AlvysOptions _options = options.Value;

    public Task<AlvysLoadsResponse> SearchLoadsAsync(
        int page = 1, int pageSize = 100, string? status = null, CancellationToken ct = default)
    {
        var body = new LoadSearchRequest
        {
            Page = Math.Max(0, page - 1),
            PageSize = pageSize,
            Status = string.IsNullOrWhiteSpace(status) ? LoadSearchRequest.AllStatuses : [status],
        };

        return SearchLoadsAsync(body, ct);
    }

    public async Task<AlvysLoadsResponse> SearchLoadsAsync(
        LoadSearchRequest request, CancellationToken ct = default)
    {
        request.Validate();
        var path = AlvysApiRoutes.LoadsSearch(_options.ApiVersion);
        return await PostSearchAsync<LoadSearchRequest, AlvysLoadsResponse>(path, request, ct)
            ?? new AlvysLoadsResponse();
    }

    public async Task<AlvysLoad?> GetLoadByNumberAsync(string loadNumber, CancellationToken ct = default)
    {
        var body = new LoadSearchRequest { Page = 0, PageSize = 1, LoadNumbers = [loadNumber] };
        var response = await SearchLoadsAsync(body, ct);
        return response.Items.FirstOrDefault();
    }

    public Task<AlvysLoad?> GetLoadAsync(LoadLookup lookup, CancellationToken ct = default)
    {
        var path = AlvysApiRoutes.LoadDetail(_options.ApiVersion, lookup);
        return GetAsync<AlvysLoad>(path, ct);
    }

    public Task<AlvysTrip?> GetTripAsync(TripLookup lookup, CancellationToken ct = default)
    {
        var path = AlvysApiRoutes.TripDetail(_options.ApiVersion, lookup);
        return GetAsync<AlvysTrip>(path, ct);
    }

    public async Task<IReadOnlyList<AlvysTripStopDetail>> ListTripStopsAsync(
        string tripId, CancellationToken ct = default)
    {
        var path = AlvysApiRoutes.TripStops(_options.ApiVersion, tripId);
        return await GetListAsync<AlvysTripStopDetail>(path, ct) ?? [];
    }

    public async Task<IReadOnlyList<AlvysLoadDocument>> ListLoadDocumentsAsync(
        string loadNumber, CancellationToken ct = default)
    {
        var path = AlvysApiRoutes.LoadDocuments(_options.ApiVersion, loadNumber);
        return await GetListAsync<AlvysLoadDocument>(path, ct) ?? [];
    }

    public async Task<IReadOnlyList<AlvysLoadNote>> ListLoadNotesAsync(
        string loadNumber, CancellationToken ct = default)
    {
        var path = AlvysApiRoutes.LoadNotes(_options.ApiVersion, loadNumber);
        return await GetListAsync<AlvysLoadNote>(path, ct) ?? [];
    }

    public async Task<AlvysTripsResponse> SearchTripsAsync(
        TripSearchRequest request, CancellationToken ct = default)
    {
        request.Validate();
        var path = AlvysApiRoutes.TripsSearch(_options.ApiVersion);
        return await PostSearchAsync<TripSearchRequest, AlvysTripsResponse>(path, request, ct)
            ?? new AlvysTripsResponse();
    }

    public async Task<AlvysTrailersResponse> SearchTrailersAsync(
        TrailerSearchRequest request, CancellationToken ct = default)
    {
        request.Validate();
        var path = AlvysApiRoutes.TrailersSearch(_options.ApiVersion);
        return await PostSearchAsync<TrailerSearchRequest, AlvysTrailersResponse>(path, request, ct)
            ?? new AlvysTrailersResponse();
    }

    public async Task<AlvysTrucksResponse> SearchTrucksAsync(
        TruckSearchRequest request, CancellationToken ct = default)
    {
        request.Validate();
        var path = AlvysApiRoutes.TrucksSearch(_options.ApiVersion);
        return await PostSearchAsync<TruckSearchRequest, AlvysTrucksResponse>(path, request, ct)
            ?? new AlvysTrucksResponse();
    }

    public async Task<IReadOnlyList<AlvysDispatchPreference>> SearchDispatchPreferencesAsync(
        DispatchPreferenceSearchRequest request, CancellationToken ct = default)
    {
        var path = AlvysApiRoutes.DispatchPreferencesSearch(_options.ApiVersion);
        return await PostSearchAsync<DispatchPreferenceSearchRequest, List<AlvysDispatchPreference>>(path, request, ct)
            ?? [];
    }

    public async Task<AlvysLocationsResponse> SearchLocationsAsync(
        LocationSearchRequest request, CancellationToken ct = default)
    {
        request.Validate();
        var path = AlvysApiRoutes.LocationsSearch(_options.ApiVersion);
        return await PostSearchAsync<LocationSearchRequest, AlvysLocationsResponse>(path, request, ct)
            ?? new AlvysLocationsResponse();
    }

    public async Task<AlvysDriversResponse> SearchDriversAsync(
        DriverSearchRequest request, CancellationToken ct = default)
    {
        request.Validate();
        var path = AlvysApiRoutes.DriversSearch(_options.ApiVersion);
        return await PostSearchAsync<DriverSearchRequest, AlvysDriversResponse>(path, request, ct)
            ?? new AlvysDriversResponse();
    }

    public async Task<AlvysCustomersResponse> SearchCustomersAsync(
        CustomerSearchRequest request, CancellationToken ct = default)
    {
        request.Validate();
        var path = AlvysApiRoutes.CustomersSearch(_options.ApiVersion);
        return await PostSearchAsync<CustomerSearchRequest, AlvysCustomersResponse>(path, request, ct)
            ?? new AlvysCustomersResponse();
    }

    public async Task<AlvysUsersResponse> SearchUsersAsync(
        UserSearchRequest request, CancellationToken ct = default)
    {
        request.Validate();
        var path = AlvysApiRoutes.UsersSearch(_options.ApiVersion);
        return await PostSearchAsync<UserSearchRequest, AlvysUsersResponse>(path, request, ct)
            ?? new AlvysUsersResponse();
    }

    public async Task<AlvysTendersResponse> SearchTendersAsync(
        TenderSearchRequest request, CancellationToken ct = default)
    {
        request.Validate();
        var path = AlvysApiRoutes.TendersSearch(_options.ApiVersion);
        return await PostSearchAsync<TenderSearchRequest, AlvysTendersResponse>(path, request, ct)
            ?? new AlvysTendersResponse();
    }

    public Task<AlvysTender?> GetTenderByIdAsync(string tenderId, CancellationToken ct = default)
    {
        var path = AlvysApiRoutes.TenderById(_options.ApiVersion, tenderId);
        return GetAsync<AlvysTender>(path, ct);
    }

    public async Task<AlvysInvoicesResponse> SearchInvoicesAsync(
        InvoiceSearchRequest request, CancellationToken ct = default)
    {
        request.Validate();
        var path = AlvysApiRoutes.InvoicesSearch(_options.ApiVersion);
        return await PostSearchAsync<InvoiceSearchRequest, AlvysInvoicesResponse>(path, request, ct)
            ?? new AlvysInvoicesResponse();
    }

    public Task<AlvysInvoice?> GetInvoiceAsync(InvoiceLookup lookup, CancellationToken ct = default)
    {
        var path = AlvysApiRoutes.InvoiceDetail(_options.ApiVersion, lookup);
        return GetAsync<AlvysInvoice>(path, ct);
    }

    public async Task<IReadOnlyList<AlvysVisibilityHistoryEvent>> ListInboundVisibilityHistoryAsync(
        string loadNumber, CancellationToken ct = default)
    {
        var path = AlvysApiRoutes.VisibilityInboundHistory(_options.ApiVersion, loadNumber);
        return await GetListAsync<AlvysVisibilityHistoryEvent>(path, ct) ?? [];
    }

    public async Task<IReadOnlyList<AlvysVisibilityHistoryEvent>> ListOutboundVisibilityHistoryAsync(
        string loadNumber, CancellationToken ct = default)
    {
        var path = AlvysApiRoutes.VisibilityOutboundHistory(_options.ApiVersion, loadNumber);
        return await GetListAsync<AlvysVisibilityHistoryEvent>(path, ct) ?? [];
    }

    public async Task<IReadOnlyList<AlvysTruckEvent>> SearchTruckEventsAsync(
        TruckEventSearchRequest request, CancellationToken ct = default)
    {
        request.Validate();
        var path = AlvysApiRoutes.TruckEventsSearch(_options.ApiVersion);
        return await PostListAsync<TruckEventSearchRequest, AlvysTruckEvent>(path, request, ct) ?? [];
    }

    public async Task<IReadOnlyList<AlvysTrailerEvent>> SearchTrailerEventsAsync(
        TrailerEventSearchRequest request, CancellationToken ct = default)
    {
        request.Validate();
        var path = AlvysApiRoutes.TrailerEventsSearch(_options.ApiVersion);
        return await PostListAsync<TrailerEventSearchRequest, AlvysTrailerEvent>(path, request, ct) ?? [];
    }

    private async Task<TResponse?> GetAsync<TResponse>(string path, CancellationToken ct)
        where TResponse : class
    {
        try
        {
            var client = httpClientFactory.CreateClient(ApiHttpClientName);
            var token = await tokenProvider.GetAccessTokenAsync(ct);

            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await client.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    "Alvys {Path} failed with HTTP {StatusCode}.", path, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // Log the exception type/message but not credentials or payloads.
            logger.LogError(ex, "Alvys {Path} transport error: {Message}", path, ex.Message);
            return null;
        }
    }

    private async Task<TResponse?> PostSearchAsync<TRequest, TResponse>(
        string path, TRequest body, CancellationToken ct)
        where TResponse : class, new()
    {
        try
        {
            var client = httpClientFactory.CreateClient(ApiHttpClientName);
            var token = await tokenProvider.GetAccessTokenAsync(ct);

            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(body),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await client.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return new TResponse();

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    "Alvys {Path} failed with HTTP {StatusCode}.", path, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // Log the exception type/message but not credentials or payloads.
            logger.LogError(ex, "Alvys {Path} transport error: {Message}", path, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Issues a read-only <c>GET</c> against an Alvys endpoint that returns a bare JSON
    /// array. Mirrors <see cref="PostSearchAsync"/>'s safety stance: 404 yields an empty
    /// list, other non-success statuses are logged (status only) and surfaced as
    /// <c>null</c> so callers degrade to an empty list.
    /// </summary>
    private async Task<List<T>?> GetListAsync<T>(string path, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient(ApiHttpClientName);
            var token = await tokenProvider.GetAccessTokenAsync(ct);

            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await client.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return [];

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    "Alvys {Path} failed with HTTP {StatusCode}.", path, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<List<T>>(JsonOptions, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // Log the exception type/message but not credentials or payloads.
            logger.LogError(ex, "Alvys {Path} transport error: {Message}", path, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Issues a read-only <c>POST</c> (filter body) against an Alvys endpoint that returns a
    /// bare JSON array — e.g. the truck/trailer event searches. Mirrors <see cref="GetListAsync"/>'s
    /// safety stance: 404 yields an empty list, other non-success statuses are logged (status
    /// only) and surfaced as <c>null</c> so callers degrade to an empty list.
    /// </summary>
    private async Task<List<T>?> PostListAsync<TRequest, T>(string path, TRequest body, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient(ApiHttpClientName);
            var token = await tokenProvider.GetAccessTokenAsync(ct);

            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(body),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await client.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return [];

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    "Alvys {Path} failed with HTTP {StatusCode}.", path, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<List<T>>(JsonOptions, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // Log the exception type/message but not credentials or payloads.
            logger.LogError(ex, "Alvys {Path} transport error: {Message}", path, ex.Message);
            return null;
        }
    }
}
