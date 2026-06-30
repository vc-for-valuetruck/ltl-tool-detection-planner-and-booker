using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Integrations.Alvys.Writeback;

/// <summary>
/// Executes live Alvys sandbox write calls for operations that have
/// <see cref="AlvysLiveSupport.Supported"/> and a fully configured sandbox. The gateway resolves
/// disposition and sets <see cref="AlvysOperationOutcome.SandboxExecutionEligible"/>; this client
/// performs the actual HTTP mutation when the recorder sees that flag.
///
/// <para>
/// Security: no bearer tokens, OAuth credentials, or Authorization headers are ever stored or
/// logged. The client logs the HTTP status code only — never response bodies, which may echo
/// request parameters including secrets.
/// </para>
/// </summary>
public interface IAlvysWriteClient
{
    Task<AlvysWriteCallResult> ExecuteAsync(
        AlvysWriteOperationDescriptor op,
        AlvysOperationRequest request,
        AlvysOperationPayload payload,
        CancellationToken ct = default);
}

/// <inheritdoc cref="IAlvysWriteClient"/>
public sealed class AlvysHttpWriteClient(
    IHttpClientFactory httpClientFactory,
    IAlvysTokenProvider tokenProvider,
    IOptions<AlvysWriteOptions> writeOptions,
    IOptions<AlvysOptions> alvysOptions,
    ILogger<AlvysHttpWriteClient> logger) : IAlvysWriteClient
{
    /// <summary>Named client used for Alvys sandbox write calls.</summary>
    public const string SandboxHttpClientName = "AlvysSandboxWrite";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    private readonly AlvysWriteOptions _write = writeOptions.Value;
    private readonly AlvysOptions _alvys = alvysOptions.Value;

    public async Task<AlvysWriteCallResult> ExecuteAsync(
        AlvysWriteOperationDescriptor op,
        AlvysOperationRequest request,
        AlvysOperationPayload payload,
        CancellationToken ct = default)
    {
        try
        {
            var client = CreateSandboxClient();
            var token = await tokenProvider.GetAccessTokenAsync(ct);
            var version = _alvys.ApiVersion;

            using var httpRequest = BuildRequest(op, request, payload, version, token);
            using var response = await client.SendAsync(httpRequest, ct);

            var statusCode = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                // Log status only — never body, which may echo secrets.
                logger.LogError(
                    "Alvys sandbox write {OperationCode} failed with HTTP {StatusCode}.",
                    op.Code, statusCode);

                return new AlvysWriteCallResult
                {
                    IsSuccess = false,
                    StatusCode = statusCode,
                    Error = $"HTTP {statusCode}",
                };
            }

            var etag = response.Headers.ETag?.Tag;
            string? body = null;
            if (response.Content.Headers.ContentLength is > 0 ||
                response.Content.Headers.ContentType is not null)
            {
                body = await response.Content.ReadAsStringAsync(ct);
                if (body.Length > 4000) body = body[..4000];
            }

            logger.LogInformation(
                "Alvys sandbox write {OperationCode} succeeded with HTTP {StatusCode}.",
                op.Code, statusCode);

            return new AlvysWriteCallResult
            {
                IsSuccess = true,
                StatusCode = statusCode,
                ETag = etag,
                Body = body,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            logger.LogError(ex, "Alvys sandbox write {OperationCode} transport error.", op.Code);
            return new AlvysWriteCallResult
            {
                IsSuccess = false,
                StatusCode = 0,
                Error = ex.GetType().Name,
            };
        }
    }

    private HttpClient CreateSandboxClient()
    {
        var client = httpClientFactory.CreateClient(SandboxHttpClientName);
        // Override base address to sandbox URL if configured; the named client is registered with
        // the production base address so writes must redirect to the sandbox.
        if (_write.HasSandboxBaseUrl)
            client.BaseAddress = new Uri(_write.SandboxBaseUrl!.TrimEnd('/') + "/");
        return client;
    }

    private static HttpRequestMessage BuildRequest(
        AlvysWriteOperationDescriptor op,
        AlvysOperationRequest request,
        AlvysOperationPayload payload,
        string? apiVersion,
        string token)
    {
        var (method, path) = ResolveEndpoint(op, request, apiVersion);

        // Build the wire body from the deterministic payload. For create-note, Alvys requires a
        // client-supplied Id; it is generated here (not in the gateway payload) so it never enters
        // the idempotency hash — two equivalent note requests must still de-duplicate.
        var wireBody = new Dictionary<string, object?>(payload.Body);
        if (op.Kind == AlvysWriteOperationKind.CreateLoadNote && !wireBody.ContainsKey("Id"))
            wireBody["Id"] = Guid.NewGuid().ToString();

        var httpRequest = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(wireBody),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // ETag-gated operations require the If-Match header for optimistic concurrency.
        if (op.RequiresEtag && !string.IsNullOrWhiteSpace(request.Etag))
            httpRequest.Headers.TryAddWithoutValidation("If-Match", request.Etag);

        return httpRequest;
    }

    private static (HttpMethod Method, string Path) ResolveEndpoint(
        AlvysWriteOperationDescriptor op, AlvysOperationRequest request, string? apiVersion)
    {
        return op.Kind switch
        {
            AlvysWriteOperationKind.CreateLoadNote =>
                (HttpMethod.Post, AlvysApiRoutes.CreateLoadNote(apiVersion, request.LoadNumber!)),

            AlvysWriteOperationKind.TenderAccept =>
                (HttpMethod.Post, AlvysApiRoutes.TenderAccept(apiVersion, request.TenderId!)),

            // Arrival and departure are separate sub-resource endpoints; both use PUT.
            AlvysWriteOperationKind.TripStopArrival =>
                (HttpMethod.Put, AlvysApiRoutes.TripStopArrival(apiVersion, request.TripId!, request.StopId!)),

            AlvysWriteOperationKind.TripStopDeparture =>
                (HttpMethod.Put, AlvysApiRoutes.TripStopDeparture(apiVersion, request.TripId!, request.StopId!)),

            AlvysWriteOperationKind.LoadUpdate =>
                (HttpMethod.Patch, AlvysApiRoutes.LoadPatch(apiVersion, request.LoadNumber!)),

            AlvysWriteOperationKind.TripAssign =>
                (HttpMethod.Post, AlvysApiRoutes.TripAssign(apiVersion, request.TripId!)),

            AlvysWriteOperationKind.TripDispatch =>
                (HttpMethod.Post, AlvysApiRoutes.TripDispatch(apiVersion, request.TripId!)),

            AlvysWriteOperationKind.CarrierStatusUpdate =>
                (HttpMethod.Patch, AlvysApiRoutes.CarrierStatusPatch(apiVersion, request.CarrierId!)),

            _ => throw new InvalidOperationException($"No endpoint mapping for operation kind {op.Kind}."),
        };
    }
}

/// <summary>
/// No-op <see cref="IAlvysWriteClient"/> for unit tests. Returns a successful result without
/// making any network calls.
/// </summary>
public sealed class NoOpAlvysWriteClient : IAlvysWriteClient
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
