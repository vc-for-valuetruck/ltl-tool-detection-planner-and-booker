using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Integrations.Yard;

/// <summary>
/// Reads the yard's physical-presence signal for a piece of equipment / driver. Read-only against the
/// yard — this client never writes. Results are cached briefly (presence is volatile) and every failure
/// mode degrades honestly rather than fabricating a pass:
/// <list type="bullet">
///   <item>Unconfigured yard → <c>null</c> (logged once at info), never a throw at startup.</item>
///   <item>5xx / timeout / transport error → <c>null</c> (presence unavailable).</item>
///   <item>404 → <see cref="YardPresence.NotOnRecord"/> (the yard answered; it has no record).</item>
/// </list>
/// </summary>
public interface IYardPresenceClient
{
    /// <summary>True when the yard is configured, so a live presence lookup can be attempted.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Returns the yard presence for the given equipment/driver ids, or null when presence is
    /// unavailable (unconfigured / unreachable). At least one id should be supplied; when all are blank
    /// the result is null without a call.
    /// </summary>
    Task<YardPresence?> GetPresenceAsync(
        string? tractorId, string? trailerId, string? driverId, CancellationToken ct = default);

    /// <summary>
    /// Drops any cached presence for the given ids so the next read re-fetches. Called when a webhook
    /// tells us the yard state changed (TruckArrived / LoadReleased).
    /// </summary>
    void InvalidatePresence(string? tractorId, string? trailerId, string? driverId);
}

/// <inheritdoc cref="IYardPresenceClient"/>
public sealed class YardPresenceClient(
    IHttpClientFactory httpClientFactory,
    IYardTokenProvider tokenProvider,
    IMemoryCache cache,
    IOptions<YardOptions> options,
    ILogger<YardPresenceClient> logger) : IYardPresenceClient
{
    /// <summary>Named client used for Yard presence (non-auth) calls.</summary>
    public const string ApiHttpClientName = "YardApi";

    private const string CacheKeyPrefix = "yard-presence:";

    private readonly YardOptions _options = options.Value;

    public bool IsConfigured => _options.IsConfigured;

    public async Task<YardPresence?> GetPresenceAsync(
        string? tractorId, string? trailerId, string? driverId, CancellationToken ct = default)
    {
        var key = CacheKey(tractorId, trailerId, driverId);
        if (key is null)
            return null;

        if (!_options.IsConfigured)
        {
            // Never throw at startup / in unconfigured deployments — presence is simply unavailable.
            logger.LogInformation("Yard presence requested but the Yard integration is not configured.");
            return null;
        }

        if (cache.TryGetValue(key, out YardPresence? cached))
            return cached;

        var presence = await FetchAsync(tractorId, trailerId, driverId, ct);

        // Cache both a live snapshot and the NotOnRecord sentinel; only an unavailable (null) result is
        // left uncached so a transient outage self-heals on the next read.
        if (presence is not null)
        {
            cache.Set(key, presence, TimeSpan.FromSeconds(Math.Max(1, _options.PresenceCacheSeconds)));
        }

        return presence;
    }

    public void InvalidatePresence(string? tractorId, string? trailerId, string? driverId)
    {
        var key = CacheKey(tractorId, trailerId, driverId);
        if (key is not null)
            cache.Remove(key);
    }

    private async Task<YardPresence?> FetchAsync(
        string? tractorId, string? trailerId, string? driverId, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient(ApiHttpClientName);
            var token = await tokenProvider.GetAccessTokenAsync(ct);

            var query = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(tractorId)) query.Add($"tractor={Uri.EscapeDataString(tractorId)}");
            if (!string.IsNullOrWhiteSpace(trailerId)) query.Add($"trailer={Uri.EscapeDataString(trailerId)}");
            if (!string.IsNullOrWhiteSpace(driverId)) query.Add($"driverId={Uri.EscapeDataString(driverId)}");
            var path = $"api/yard/presence?{string.Join('&', query)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await client.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return YardPresence.NotOnRecord;

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    "Yard presence lookup failed with HTTP {StatusCode}.", (int)response.StatusCode);
                return null;
            }

            var wire = await response.Content.ReadFromJsonAsync<YardPresenceWire>(ct);
            return wire is null ? null : wire.ToPresence();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // Log the exception type/message but not credentials. Presence is unavailable.
            logger.LogError(ex, "Yard presence transport error: {Message}", ex.Message);
            return null;
        }
    }

    private static string? CacheKey(string? tractorId, string? trailerId, string? driverId)
    {
        var t = tractorId?.Trim() ?? "";
        var r = trailerId?.Trim() ?? "";
        var d = driverId?.Trim() ?? "";
        if (t.Length == 0 && r.Length == 0 && d.Length == 0)
            return null;
        return $"{CacheKeyPrefix}{t}|{r}|{d}";
    }
}

/// <summary>Wire shape of the Yard presence response. Tolerant of missing fields — all optional.</summary>
internal sealed record YardPresenceWire
{
    [JsonPropertyName("atYard")] public bool AtYard { get; init; }
    [JsonPropertyName("releasedAt")] public DateTimeOffset? ReleasedAt { get; init; }
    [JsonPropertyName("driverPresent")] public bool DriverPresent { get; init; }
    [JsonPropertyName("securityHold")] public bool SecurityHold { get; init; }
    [JsonPropertyName("lastEventAt")] public DateTimeOffset? LastEventAt { get; init; }
    [JsonPropertyName("gates")] public YardGatesWire? Gates { get; init; }

    public YardPresence ToPresence() => new()
    {
        AtYard = AtYard,
        ReleasedAt = ReleasedAt,
        DriverPresent = DriverPresent,
        SecurityHold = SecurityHold,
        LastEventAt = LastEventAt,
        Gates = Gates is null
            ? PhotoGates.None
            : new PhotoGates(Gates.Tractor, Gates.Trailer, Gates.Seal),
        OnRecord = true,
    };
}

internal sealed record YardGatesWire
{
    [JsonPropertyName("tractor")] public bool Tractor { get; init; }
    [JsonPropertyName("trailer")] public bool Trailer { get; init; }
    [JsonPropertyName("seal")] public bool Seal { get; init; }
}
