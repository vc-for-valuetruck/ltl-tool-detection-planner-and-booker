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

    public async Task<AlvysTripsResponse> SearchTripsAsync(
        TripSearchRequest request, CancellationToken ct = default)
    {
        request.Validate();
        var path = AlvysApiRoutes.TripsSearch(_options.ApiVersion);
        return await PostSearchAsync<TripSearchRequest, AlvysTripsResponse>(path, request, ct)
            ?? new AlvysTripsResponse();
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
}
