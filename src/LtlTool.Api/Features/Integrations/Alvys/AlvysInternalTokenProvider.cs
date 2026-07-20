using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Integrations.Alvys;

/// <summary>
/// Acquires and caches a per-acting-user Alvys <b>internal-API</b> session token (decision #10 in
/// <c>docs/ALVYS_API_DECISIONS.md</c>). Unlike the Public API, the internal API authenticates with an
/// active user's Auth0 session token — not the machine-to-machine client-credentials token. The token
/// expires, so the tool must hold a valid one per acting dispatcher and re-acquire on expiry.
///
/// <para>
/// The token is cached keyed by acting user so concurrent operations for the same dispatcher reuse a
/// single acquisition. <see cref="InvalidateToken"/> forces the next call to re-acquire — the internal
/// write client calls it exactly once when a write returns a <c>token_expired</c> signal, then retries.
/// </para>
/// </summary>
public interface IAlvysInternalTokenProvider
{
    /// <summary>
    /// Returns a valid session token for the acting user, acquiring a fresh one when the cache is
    /// empty or expired. Throws <see cref="InvalidOperationException"/> when the internal API is
    /// disabled/misconfigured or the auth endpoint rejects the request.
    /// </summary>
    Task<string> GetSessionTokenAsync(string actingUserId, CancellationToken ct = default);

    /// <summary>
    /// Drops any cached token for the acting user so the next <see cref="GetSessionTokenAsync"/>
    /// re-authenticates. Used for the single re-auth retry after a <c>token_expired</c> response.
    /// </summary>
    void InvalidateToken(string actingUserId);
}

/// <summary>
/// Default <see cref="IAlvysInternalTokenProvider"/>. Acquires a session token by POSTing the acting
/// user's identity to the configured internal-auth host (the headless-login helper of decision #10)
/// and caches it until shortly before expiry.
///
/// <para>
/// Security: the token is held in memory only, never logged and never persisted to the outbox.
/// Acquisition failures log the status code and a redacted message only — never the response body,
/// which can echo auth material.
/// </para>
/// </summary>
public sealed class AlvysInternalTokenProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<AlvysInternalApiOptions> options,
    ILogger<AlvysInternalTokenProvider> logger) : IAlvysInternalTokenProvider
{
    /// <summary>Named client used exclusively for internal-API session-token requests.</summary>
    public const string AuthHttpClientName = "AlvysInternalAuth";

    private static readonly TimeSpan ExpiryBuffer = TimeSpan.FromSeconds(60);

    private readonly AlvysInternalApiOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, CachedToken> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<string> GetSessionTokenAsync(string actingUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actingUserId))
            throw new InvalidOperationException("An acting user id is required to acquire an internal-API session token.");

        if (_cache.TryGetValue(actingUserId, out var cached) && DateTimeOffset.UtcNow < cached.ExpiresAt)
            return cached.Token;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(actingUserId, out cached) && DateTimeOffset.UtcNow < cached.ExpiresAt)
                return cached.Token;

            if (!_options.Enabled)
                throw new InvalidOperationException(
                    "The Alvys internal API is disabled (Alvys:InternalApi:Enabled=false).");
            if (!_options.HasBaseUrl)
                throw new InvalidOperationException(
                    "No Alvys internal API base URL is configured (Alvys:InternalApi:BaseUrl).");

            logger.LogInformation("Acquiring Alvys internal-API session token for the acting user.");

            var token = await AcquireAsync(actingUserId, ct);
            _cache[actingUserId] = token;
            return token.Token;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void InvalidateToken(string actingUserId)
    {
        if (!string.IsNullOrWhiteSpace(actingUserId))
            _cache.TryRemove(actingUserId, out _);
    }

    private async Task<CachedToken> AcquireAsync(string actingUserId, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(AuthHttpClientName);
        var authRoot = _options.EffectiveAuthBaseUrl.TrimEnd('/');

        // NOTE: the internal-auth endpoint is observed-not-contracted and its exact refresh contract
        // is still pending discovery (decision #10). This path is a documented placeholder — the
        // scaffolding proves the acquire/cache/invalidate flow end-to-end against a fake handler.
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{authRoot}/auth/session")
        {
            Content = JsonContent.Create(new { actingUserId }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            // Deliberately do NOT log the body — it can echo auth material.
            logger.LogError(
                "Alvys internal-API session token request failed with HTTP {StatusCode}.",
                (int)response.StatusCode);
            throw new InvalidOperationException(
                $"Alvys internal-API session token request failed with HTTP {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<AlvysInternalSessionTokenResponse>(ct)
            ?? throw new InvalidOperationException("Alvys internal-API session token response was empty.");
        if (string.IsNullOrWhiteSpace(payload.AccessToken))
            throw new InvalidOperationException("Alvys internal-API session token response omitted the token.");

        var ttl = payload.ExpiresIn > 0 ? payload.ExpiresIn : _options.TokenTtlSeconds;
        return new CachedToken(payload.AccessToken, DateTimeOffset.UtcNow.AddSeconds(ttl) - ExpiryBuffer);
    }

    private readonly record struct CachedToken(string Token, DateTimeOffset ExpiresAt);
}

/// <summary>Wire model for the internal-API session-token response.</summary>
public sealed class AlvysInternalSessionTokenResponse
{
    public string AccessToken { get; set; } = "";
    public int ExpiresIn { get; set; }
}
