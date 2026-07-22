using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Integrations.Yard;

/// <summary>
/// Acquires and caches Yard OAuth2 access tokens (Entra ID client-credentials grant).
/// </summary>
public interface IYardTokenProvider
{
    /// <summary>
    /// Returns a valid bearer token, fetching a new one when the cache is empty or expired. Throws
    /// <see cref="InvalidOperationException"/> when credentials are missing or the token endpoint
    /// rejects the request.
    /// </summary>
    Task<string> GetAccessTokenAsync(CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="IYardTokenProvider"/>: posts client credentials to the Yard's Entra token
/// endpoint (form-encoded, per the Entra client-credentials contract) and caches the access token
/// until shortly before expiry. Mirrors <c>AlvysTokenProvider</c>.
///
/// Secrets are never logged — failures log the status code and a redacted message only.
/// </summary>
public sealed class YardTokenProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<YardOptions> options,
    ILogger<YardTokenProvider> logger) : IYardTokenProvider
{
    /// <summary>Named client used exclusively for token requests.</summary>
    public const string AuthHttpClientName = "YardAuth";

    private static readonly TimeSpan ExpiryBuffer = TimeSpan.FromSeconds(60);

    private readonly YardOptions _options = options.Value;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string? _cachedToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _expiresAt)
            return _cachedToken;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cachedToken is not null && DateTimeOffset.UtcNow < _expiresAt)
                return _cachedToken;

            if (!_options.IsConfigured)
            {
                throw new InvalidOperationException(
                    "Yard credentials are not configured (Yard:BaseUrl / Yard:ClientId / Yard:ClientSecret).");
            }

            logger.LogInformation("Requesting new Yard OAuth2 access token.");

            var httpClient = httpClientFactory.CreateClient(AuthHttpClientName);
            var form = new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["scope"] = _options.ApiScope,
                ["grant_type"] = "client_credentials",
            };

            using var content = new FormUrlEncodedContent(form);
            using var response = await httpClient.PostAsync(_options.TokenUrl, content, ct);
            if (!response.IsSuccessStatusCode)
            {
                // Deliberately do NOT log the response body — it can echo request parameters.
                logger.LogError(
                    "Yard token request failed with HTTP {StatusCode}.", (int)response.StatusCode);
                throw new InvalidOperationException(
                    $"Yard OAuth2 token request failed with HTTP {(int)response.StatusCode}.");
            }

            var token = await response.Content.ReadFromJsonAsync<YardTokenResponse>(ct)
                ?? throw new InvalidOperationException("Yard token response was empty.");

            _cachedToken = token.AccessToken;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn) - ExpiryBuffer;

            logger.LogInformation("Yard access token acquired; expires in {ExpiresIn}s.", token.ExpiresIn);

            return _cachedToken;
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>OAuth2 token response from the Yard's Entra token endpoint.</summary>
internal sealed record YardTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("token_type")] string? TokenType);
