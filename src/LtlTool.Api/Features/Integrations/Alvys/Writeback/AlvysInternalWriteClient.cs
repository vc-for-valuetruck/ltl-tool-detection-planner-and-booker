using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Integrations.Alvys.Writeback;

/// <summary>
/// Well-known signals for the Alvys internal-API <c>token_expired</c> condition. The internal API
/// authenticates with a per-acting-user session token that expires (decision #10); when a write is
/// rejected because the token lapsed, the client must re-authenticate <b>exactly once</b> and retry,
/// then surface an honest failure. These helpers centralise how that condition is recognised so the
/// write client and its regression tests agree on the contract.
/// </summary>
public static class AlvysInternalTokenSignals
{
    /// <summary>The observed error code Alvys returns when a session token has expired.</summary>
    public const string TokenExpired = "token_expired";

    /// <summary>
    /// True when a response indicates the session token expired: an HTTP 401, or a body that carries
    /// the <see cref="TokenExpired"/> marker. Kept deliberately conservative — only these signals
    /// trigger the single re-auth retry; any other non-2xx is a plain failure.
    /// </summary>
    public static bool IndicatesTokenExpired(HttpStatusCode statusCode, string? body)
    {
        if (statusCode == HttpStatusCode.Unauthorized)
            return true;
        return !string.IsNullOrEmpty(body)
            && body.Contains(TokenExpired, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Executes live Alvys <b>internal-API</b> write calls for the Phase-2 consolidation operations
/// (<c>add-extended-stop</c>, <c>zero-child-dispatch-miles</c>, <c>set-trip-references</c>). Unlike
/// <see cref="IAlvysWriteClient"/> (Public API, client-credentials auth), this client authenticates
/// with the acting user's session token from <see cref="IAlvysInternalTokenProvider"/> and performs
/// a single re-authentication retry on a <c>token_expired</c> signal.
///
/// <para>
/// SCAFFOLDING: the internal endpoints are observed-not-contracted and still pending discovery
/// (decision #10). Nothing reaches a live Alvys tenant in this phase — the recorder only invokes
/// this client for operations that are both <see cref="AlvysLiveSupport.Supported"/> and armed, and
/// none of the internal operations are Supported yet. The wiring is exercised end-to-end against a
/// fake handler in tests.
/// </para>
///
/// <para>
/// Security: the session token is attached as a bearer header only; it is never logged, and neither
/// are response bodies (which can echo auth material). Only status codes are logged.
/// </para>
/// </summary>
public interface IAlvysInternalWriteClient
{
    Task<AlvysWriteCallResult> ExecuteAsync(
        AlvysWriteOperationDescriptor op,
        AlvysOperationRequest request,
        AlvysOperationPayload payload,
        CancellationToken ct = default);
}

/// <inheritdoc cref="IAlvysInternalWriteClient"/>
public sealed class AlvysHttpInternalWriteClient(
    IHttpClientFactory httpClientFactory,
    IAlvysInternalTokenProvider tokenProvider,
    IOptions<AlvysInternalApiOptions> internalOptions,
    ILogger<AlvysHttpInternalWriteClient> logger) : IAlvysInternalWriteClient
{
    /// <summary>Named client used for Alvys internal-API write calls.</summary>
    public const string HttpClientName = "AlvysInternalWrite";

    private readonly AlvysInternalApiOptions _options = internalOptions.Value;

    public async Task<AlvysWriteCallResult> ExecuteAsync(
        AlvysWriteOperationDescriptor op,
        AlvysOperationRequest request,
        AlvysOperationPayload payload,
        CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return Failure(op, 0, "internal_api_disabled");
        if (!_options.HasBaseUrl)
            return Failure(op, 0, "internal_api_base_url_missing");
        if (string.IsNullOrWhiteSpace(request.ActingUserId))
            return Failure(op, 0, "acting_user_missing");

        // Single re-auth retry: attempt 0 uses the cached token; on a token_expired signal we
        // invalidate, re-acquire once (attempt 1) and retry. A second token_expired is an honest
        // failure — we never loop.
        for (var attempt = 0; attempt <= 1; attempt++)
        {
            AlvysWriteCallResult result;
            try
            {
                var token = await tokenProvider.GetSessionTokenAsync(request.ActingUserId!, ct);
                result = await SendAsync(op, request, payload, token, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                logger.LogError(ex, "Alvys internal write {OperationCode} transport error.", op.Code);
                return Failure(op, 0, ex.GetType().Name);
            }

            if (result.IsSuccess)
                return result;

            var tokenExpired = AlvysInternalTokenSignals.IndicatesTokenExpired(
                (HttpStatusCode)result.StatusCode, result.Body);

            if (tokenExpired && attempt == 0)
            {
                logger.LogWarning(
                    "Alvys internal write {OperationCode} hit token_expired; re-authenticating once.",
                    op.Code);
                tokenProvider.InvalidateToken(request.ActingUserId!);
                continue;
            }

            if (tokenExpired)
                return Failure(op, result.StatusCode, $"{AlvysInternalTokenSignals.TokenExpired}_after_2_attempts");

            return result;
        }

        // Unreachable — the loop always returns — but keeps the compiler satisfied.
        return Failure(op, 0, "unreachable");
    }

    private async Task<AlvysWriteCallResult> SendAsync(
        AlvysWriteOperationDescriptor op,
        AlvysOperationRequest request,
        AlvysOperationPayload payload,
        string token,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var root = _options.BaseUrl.TrimEnd('/');
        var (method, path) = ResolveEndpoint(op, request);

        using var httpRequest = new HttpRequestMessage(method, $"{root}{path}")
        {
            Content = JsonContent.Create(payload.Body),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(httpRequest, ct);
        var statusCode = (int)response.StatusCode;

        string? body = null;
        if (response.Content.Headers.ContentLength is > 0 ||
            response.Content.Headers.ContentType is not null)
        {
            body = await response.Content.ReadAsStringAsync(ct);
            if (body.Length > 4000) body = body[..4000];
        }

        if (!response.IsSuccessStatusCode)
        {
            // Log status only — never the body, which can echo auth material.
            logger.LogError(
                "Alvys internal write {OperationCode} failed with HTTP {StatusCode}.",
                op.Code, statusCode);
            return new AlvysWriteCallResult
            {
                IsSuccess = false,
                StatusCode = statusCode,
                Body = body,
                Error = $"HTTP {statusCode}",
            };
        }

        logger.LogInformation(
            "Alvys internal write {OperationCode} succeeded with HTTP {StatusCode}.",
            op.Code, statusCode);

        return new AlvysWriteCallResult
        {
            IsSuccess = true,
            StatusCode = statusCode,
            ETag = response.Headers.ETag?.Tag,
            Body = body,
        };
    }

    // NOTE: these routes are observed-not-contracted placeholders (decision #10). They are only ever
    // exercised against a fake handler in tests; no internal operation is Supported for live
    // execution, so the recorder never dispatches here against a real Alvys tenant in this phase.
    private static (HttpMethod Method, string Path) ResolveEndpoint(
        AlvysWriteOperationDescriptor op, AlvysOperationRequest request) => op.Kind switch
    {
        AlvysWriteOperationKind.AddExtendedStop =>
            (HttpMethod.Post, $"/internal/trips/{request.TripId}/waypoints"),
        AlvysWriteOperationKind.ZeroChildDispatchMiles =>
            (HttpMethod.Patch, $"/internal/trips/{request.TripId}/mileage"),
        AlvysWriteOperationKind.SetTripReferences =>
            (HttpMethod.Patch, $"/internal/trips/{request.TripId}/references"),
        _ => throw new InvalidOperationException(
            $"No internal endpoint mapping for operation kind {op.Kind}."),
    };

    private static AlvysWriteCallResult Failure(
        AlvysWriteOperationDescriptor op, int statusCode, string error)
    {
        return new AlvysWriteCallResult
        {
            IsSuccess = false,
            StatusCode = statusCode,
            Error = error,
        };
    }
}

/// <summary>
/// No-op <see cref="IAlvysInternalWriteClient"/> for unit tests / non-internal deployments. Returns a
/// successful result without any network call.
/// </summary>
public sealed class NoOpAlvysInternalWriteClient : IAlvysInternalWriteClient
{
    public Task<AlvysWriteCallResult> ExecuteAsync(
        AlvysWriteOperationDescriptor op,
        AlvysOperationRequest request,
        AlvysOperationPayload payload,
        CancellationToken ct = default) =>
        Task.FromResult(new AlvysWriteCallResult
        {
            IsSuccess = true,
            StatusCode = 200,
        });
}
