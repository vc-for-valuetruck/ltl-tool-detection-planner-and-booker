using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.Notifications;

/// <summary>A plain-text message to hand to the Graph <c>sendMail</c> transport.</summary>
public sealed class GraphMailMessage
{
    public required string Subject { get; init; }
    public required string Body { get; init; }
    public required IReadOnlyList<string> ToAddresses { get; init; }
}

/// <summary>
/// The outcome of one transport attempt. <see cref="Retryable"/> distinguishes a transient failure
/// (throttling / 5xx / network) that the channel should back off and retry from a permanent one
/// (bad config, 4xx auth/permission) that will never succeed on retry. Never fabricates success.
/// </summary>
public sealed class GraphMailSendOutcome
{
    public required bool Success { get; init; }
    public bool Retryable { get; init; }
    public string? Detail { get; init; }

    public static GraphMailSendOutcome Sent(string? detail = null) =>
        new() { Success = true, Detail = detail };

    public static GraphMailSendOutcome TransientFailure(string detail) =>
        new() { Success = false, Retryable = true, Detail = detail };

    public static GraphMailSendOutcome PermanentFailure(string detail) =>
        new() { Success = false, Retryable = false, Detail = detail };
}

/// <summary>
/// Transport seam over Microsoft Graph <c>sendMail</c>. Abstracted so the channel state machine can
/// be unit-tested against a fake without any network or Entra dependency. The real implementation is
/// <see cref="GraphMailClient"/>.
/// </summary>
public interface IGraphMailClient
{
    Task<GraphMailSendOutcome> SendAsync(GraphMailMessage message, CancellationToken ct);
}

/// <summary>
/// Real <see cref="IGraphMailClient"/>: acquires an app-only (client-credentials) Graph token and
/// POSTs to <c>/users/{sender}/sendMail</c>. Mirrors <c>AlvysTokenProvider</c>'s client-credentials
/// pattern (a raw token POST, no extra SDK) and its secret-hygiene: the client secret and the bearer
/// token are never logged; failures log status codes only. Requires the <c>Mail.Send</c> application
/// permission with admin consent — until that external dependency lands, sends fail permanently with
/// an honest 403 detail rather than a fabricated delivery.
/// </summary>
public sealed class GraphMailClient(
    IHttpClientFactory httpClientFactory,
    IOptions<NotificationOptions> options,
    ILogger<GraphMailClient> logger) : IGraphMailClient
{
    /// <summary>Named HttpClient used for both the token POST and the Graph sendMail POST.</summary>
    public const string HttpClientName = "GraphMail";

    private static readonly TimeSpan ExpiryBuffer = TimeSpan.FromSeconds(60);

    private readonly EmailChannelOptions _email = options.Value.Email;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string? _cachedToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public async Task<GraphMailSendOutcome> SendAsync(GraphMailMessage message, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        string token;
        try
        {
            token = await GetTokenAsync(ct);
        }
        catch (Exception ex)
        {
            // A token failure is treated as transient by default (network/throttle), but a rejected
            // grant (bad secret / consent missing) is permanent — the caller distinguishes on Retryable.
            logger.LogWarning(ex, "Graph token acquisition failed.");
            return ex is GraphAuthPermanentException
                ? GraphMailSendOutcome.PermanentFailure(ex.Message)
                : GraphMailSendOutcome.TransientFailure("Graph token acquisition failed.");
        }

        var sender = _email.FromAddress!.Trim();
        var endpoint = $"{_email.Graph.GraphBaseUrl.TrimEnd('/')}/users/{Uri.EscapeDataString(sender)}/sendMail";
        var payload = new GraphSendMailRequest
        {
            Message = new GraphMessage
            {
                Subject = message.Subject,
                Body = new GraphItemBody { ContentType = "Text", Content = message.Body },
                ToRecipients = message.ToAddresses
                    .Select(a => new GraphRecipient { EmailAddress = new GraphEmailAddress { Address = a } })
                    .ToArray(),
            },
            SaveToSentItems = false,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(payload);

        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Accepted || response.IsSuccessStatusCode)
        {
            return GraphMailSendOutcome.Sent("Sent via Microsoft Graph sendMail.");
        }

        var status = (int)response.StatusCode;
        logger.LogWarning("Graph sendMail returned HTTP {StatusCode}.", status);

        // 429 + 5xx are transient (retry with backoff). 401 could be a stale token — drop the cache so
        // the next attempt re-acquires — and is retried once. Everything else (403 consent missing, 400
        // bad mailbox) is permanent; retrying cannot help, so fail honestly and stop.
        if (response.StatusCode is HttpStatusCode.TooManyRequests || status >= 500)
        {
            return GraphMailSendOutcome.TransientFailure($"Graph sendMail returned HTTP {status}.");
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            InvalidateToken();
            return GraphMailSendOutcome.TransientFailure("Graph sendMail returned HTTP 401 (token refreshed).");
        }

        return GraphMailSendOutcome.PermanentFailure($"Graph sendMail returned HTTP {status}.");
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _expiresAt)
            return _cachedToken;

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_cachedToken is not null && DateTimeOffset.UtcNow < _expiresAt)
                return _cachedToken;

            var graph = _email.Graph;
            var http = httpClientFactory.CreateClient(HttpClientName);
            var tokenUrl = $"{graph.Instance.TrimEnd('/')}/{graph.TenantId}/oauth2/v2.0/token";
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = graph.ClientId!,
                ["client_secret"] = graph.ClientSecret!,
                ["scope"] = "https://graph.microsoft.com/.default",
                ["grant_type"] = "client_credentials",
            });

            using var response = await http.PostAsync(tokenUrl, form, ct);
            if (!response.IsSuccessStatusCode)
            {
                // Do NOT log the body — it can echo the client_secret. 4xx here means the grant itself
                // was rejected (bad secret / disabled app) → permanent.
                var status = (int)response.StatusCode;
                logger.LogError("Graph token request failed with HTTP {StatusCode}.", status);
                if (status is >= 400 and < 500)
                    throw new GraphAuthPermanentException($"Graph token request failed with HTTP {status}.");
                throw new InvalidOperationException($"Graph token request failed with HTTP {status}.");
            }

            var token = await response.Content.ReadFromJsonAsync<GraphTokenResponse>(ct)
                ?? throw new InvalidOperationException("Graph token response was empty.");

            _cachedToken = token.AccessToken;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn) - ExpiryBuffer;
            return _cachedToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private void InvalidateToken()
    {
        _cachedToken = null;
        _expiresAt = DateTimeOffset.MinValue;
    }

    /// <summary>Thrown when the token grant is rejected in a way retrying cannot fix (bad secret / consent).</summary>
    private sealed class GraphAuthPermanentException(string message) : Exception(message);

    private sealed class GraphTokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; init; } = "";
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
    }

    private sealed class GraphSendMailRequest
    {
        [JsonPropertyName("message")] public required GraphMessage Message { get; init; }
        [JsonPropertyName("saveToSentItems")] public bool SaveToSentItems { get; init; }
    }

    private sealed class GraphMessage
    {
        [JsonPropertyName("subject")] public required string Subject { get; init; }
        [JsonPropertyName("body")] public required GraphItemBody Body { get; init; }
        [JsonPropertyName("toRecipients")] public required IReadOnlyList<GraphRecipient> ToRecipients { get; init; }
    }

    private sealed class GraphItemBody
    {
        [JsonPropertyName("contentType")] public required string ContentType { get; init; }
        [JsonPropertyName("content")] public required string Content { get; init; }
    }

    private sealed class GraphRecipient
    {
        [JsonPropertyName("emailAddress")] public required GraphEmailAddress EmailAddress { get; init; }
    }

    private sealed class GraphEmailAddress
    {
        [JsonPropertyName("address")] public required string Address { get; init; }
    }
}
