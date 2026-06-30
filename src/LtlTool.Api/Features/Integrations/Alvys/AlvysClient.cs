using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace LtlTool.Api.Features.Integrations.Alvys;

/// <summary>
/// Live Alvys TMS client and the default source of truth for LTL data.
///
/// Uses a named <see cref="HttpClient"/> (base address + timeout configured at
/// registration) and attaches a bearer token from <see cref="IAlvysTokenProvider"/>
/// per request. Non-success responses are logged (status only — never bodies,
/// which may echo secrets) and surfaced as empty results / null so callers
/// degrade gracefully.
/// </summary>
public sealed class AlvysClient(
    IHttpClientFactory httpClientFactory,
    IAlvysTokenProvider tokenProvider,
    ILogger<AlvysClient> logger) : IAlvysClient
{
    /// <summary>Named client used for Alvys API (non-auth) calls.</summary>
    public const string ApiHttpClientName = "AlvysApi";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public async Task<AlvysLoadsResponse> SearchLoadsAsync(
        int page = 1, int pageSize = 100, string? status = null, CancellationToken ct = default)
    {
        var body = new LoadSearchRequest
        {
            Page = Math.Max(0, page - 1),
            PageSize = pageSize,
            Status = string.IsNullOrWhiteSpace(status) ? LoadSearchRequest.AllStatuses : [status],
        };

        return await PostSearchAsync(body, ct) ?? new AlvysLoadsResponse();
    }

    public async Task<AlvysLoad?> GetLoadByNumberAsync(string loadNumber, CancellationToken ct = default)
    {
        var body = new LoadSearchRequest { Page = 0, PageSize = 1, LoadNumbers = [loadNumber] };
        var response = await PostSearchAsync(body, ct);
        return response?.Items.FirstOrDefault();
    }

    private async Task<AlvysLoadsResponse?> PostSearchAsync(
        LoadSearchRequest body, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient(ApiHttpClientName);
            var token = await tokenProvider.GetAccessTokenAsync(ct);

            using var request = new HttpRequestMessage(HttpMethod.Post, "loads/search")
            {
                Content = JsonContent.Create(body),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await client.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return new AlvysLoadsResponse();

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    "Alvys loads/search failed with HTTP {StatusCode}.", (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<AlvysLoadsResponse>(JsonOptions, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // Log the exception type/message but not credentials or payloads.
            logger.LogError(ex, "Alvys loads/search transport error: {Message}", ex.Message);
            return null;
        }
    }
}
