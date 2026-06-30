using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Integrations.Alvys;

/// <summary>
/// Acquires and caches Alvys OAuth2 access tokens (client-credentials grant).
/// </summary>
public interface IAlvysTokenProvider
{
    /// <summary>
    /// Returns a valid bearer token, fetching a new one when the cache is empty
    /// or expired. Throws <see cref="InvalidOperationException"/> when credentials
    /// are missing or the token endpoint rejects the request.
    /// </summary>
    Task<string> GetAccessTokenAsync(CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="IAlvysTokenProvider"/>: posts client credentials to the
/// Alvys token endpoint and caches the access token until shortly before expiry.
///
/// Secrets are never logged — failures log status code and a redacted message only.
/// </summary>
public sealed class AlvysTokenProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<AlvysOptions> options,
    ILogger<AlvysTokenProvider> logger) : IAlvysTokenProvider
{
    /// <summary>Named client used exclusively for token requests.</summary>
    public const string AuthHttpClientName = "AlvysAuth";

    private static readonly TimeSpan ExpiryBuffer = TimeSpan.FromSeconds(60);

    private readonly AlvysOptions _options = options.Value;
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

            if (!_options.HasCredentials)
            {
                throw new InvalidOperationException(
                    "Alvys credentials are not configured (Alvys:ClientId / Alvys:ClientSecret). " +
                    "Set them server-side, or select the Fallback provider for local/UAT.");
            }

            logger.LogInformation("Requesting new Alvys OAuth2 access token.");

            var httpClient = httpClientFactory.CreateClient(AuthHttpClientName);
            var payload = new
            {
                client_id = _options.ClientId,
                client_secret = _options.ClientSecret,
                audience = _options.Audience,
                grant_type = "client_credentials",
            };

            using var response = await httpClient.PostAsJsonAsync(_options.TokenUrl, payload, ct);
            if (!response.IsSuccessStatusCode)
            {
                // Deliberately do NOT log the response body — it can echo request
                // parameters including the client_secret.
                logger.LogError(
                    "Alvys token request failed with HTTP {StatusCode}.", (int)response.StatusCode);
                throw new InvalidOperationException(
                    $"Alvys OAuth2 token request failed with HTTP {(int)response.StatusCode}.");
            }

            var token = await response.Content.ReadFromJsonAsync<AlvysTokenResponse>(ct)
                ?? throw new InvalidOperationException("Alvys token response was empty.");

            _cachedToken = token.AccessToken;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn) - ExpiryBuffer;

            logger.LogInformation(
                "Alvys access token acquired; expires in {ExpiresIn}s.", token.ExpiresIn);

            return _cachedToken;
        }
        finally
        {
            _lock.Release();
        }
    }
}
