using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Integrations.Alvys.Writeback;

/// <summary>
/// The result of a live multipart document/invoice upload. Captures the non-secret attachment
/// reference Alvys returns (id, path, type) so the outbox can record a <see cref="AlvysOperationRecord.ResultReference"/>
/// and the reconciler can confirm the attachment landed. No auth material is ever captured.
/// </summary>
public sealed class AlvysDocumentUploadResult
{
    public required bool IsSuccess { get; init; }
    public required int StatusCode { get; init; }

    /// <summary>The attachment id Alvys assigned (null when the response omitted it).</summary>
    public string? AttachmentId { get; init; }

    /// <summary>The non-secret storage path Alvys returned, surfaced to the dispatcher UI.</summary>
    public string? AttachmentPath { get; init; }

    /// <summary>The attachment type Alvys echoed back (should match the requested DocumentType).</summary>
    public string? AttachmentType { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// Dispatches Alvys billing-document uploads over the <b>Public API</b> as multipart/form-data.
/// This is a distinct seam from <see cref="IAlvysWriteClient"/> (JSON writes) because uploads carry
/// raw file bytes that must never enter the JSON payload/hash/preview.
///
/// <para>
/// Security: uploads authenticate with the Public-API client-credentials bearer token
/// (<see cref="IAlvysTokenProvider"/>) — never an internal-API session token. The file bytes are
/// streamed straight to Alvys and never persisted or logged; only the HTTP status code is logged.
/// </para>
/// </summary>
public interface IAlvysDocumentUploadClient
{
    Task<AlvysDocumentUploadResult> UploadAsync(
        AlvysWriteOperationDescriptor op,
        AlvysOperationRequest request,
        AlvysOperationPayload payload,
        CancellationToken ct = default);
}

/// <inheritdoc cref="IAlvysDocumentUploadClient"/>
public sealed class AlvysHttpDocumentUploadClient(
    IHttpClientFactory httpClientFactory,
    IAlvysTokenProvider tokenProvider,
    IOptions<AlvysWriteOptions> writeOptions,
    IOptions<AlvysOptions> alvysOptions,
    ILogger<AlvysHttpDocumentUploadClient> logger) : IAlvysDocumentUploadClient
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    private readonly AlvysWriteOptions _write = writeOptions.Value;
    private readonly AlvysOptions _alvys = alvysOptions.Value;

    public async Task<AlvysDocumentUploadResult> UploadAsync(
        AlvysWriteOperationDescriptor op,
        AlvysOperationRequest request,
        AlvysOperationPayload payload,
        CancellationToken ct = default)
    {
        try
        {
            var client = CreateSandboxClient();
            // Public-API client-credentials token — NOT an internal session token.
            var token = await tokenProvider.GetAccessTokenAsync(ct);
            var version = _alvys.ApiVersion;

            using var content = BuildMultipart(op, request);
            var path = ResolveEndpoint(op, request, version);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await client.SendAsync(httpRequest, ct);
            var statusCode = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                // Log status only — never body, which may echo request parameters.
                logger.LogError(
                    "Alvys document upload {OperationCode} failed with HTTP {StatusCode}.",
                    op.Code, statusCode);
                return new AlvysDocumentUploadResult
                {
                    IsSuccess = false,
                    StatusCode = statusCode,
                    Error = $"HTTP {statusCode}",
                };
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            AlvysLoadDocument? attachment = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try { attachment = JsonSerializer.Deserialize<AlvysLoadDocument>(body, JsonOptions); }
                catch (JsonException) { /* tolerated: success stands even if the body shape drifts */ }
            }

            logger.LogInformation(
                "Alvys document upload {OperationCode} succeeded with HTTP {StatusCode}.",
                op.Code, statusCode);

            return new AlvysDocumentUploadResult
            {
                IsSuccess = true,
                StatusCode = statusCode,
                AttachmentId = string.IsNullOrWhiteSpace(attachment?.Id) ? null : attachment!.Id,
                AttachmentPath = attachment?.AttachmentPath,
                AttachmentType = attachment?.AttachmentType,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            logger.LogError(ex, "Alvys document upload {OperationCode} transport error.", op.Code);
            return new AlvysDocumentUploadResult { IsSuccess = false, StatusCode = 0, Error = ex.GetType().Name };
        }
    }

    private HttpClient CreateSandboxClient()
    {
        var client = httpClientFactory.CreateClient(AlvysHttpWriteClient.SandboxHttpClientName);
        if (_write.HasSandboxBaseUrl)
            client.BaseAddress = new Uri(_write.SandboxBaseUrl!.TrimEnd('/') + "/");
        return client;
    }

    /// <summary>
    /// Builds the multipart body. Exactly one file part plus the metadata parts Alvys expects. The
    /// caller/gateway has already validated presence, size, content type, and DocumentType.
    /// </summary>
    private static MultipartFormDataContent BuildMultipart(
        AlvysWriteOperationDescriptor op, AlvysOperationRequest request)
    {
        var content = new MultipartFormDataContent();

        var fileContent = new ByteArrayContent(request.FileBytes!);
        if (!string.IsNullOrWhiteSpace(request.ContentType))
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(request.ContentType.Split(';')[0].Trim());
        var fileName = string.IsNullOrWhiteSpace(request.FileName) ? "document" : request.FileName;
        content.Add(fileContent, "File", fileName);

        if (op.Kind == AlvysWriteOperationKind.CreateCarrierInvoice)
        {
            content.Add(new StringContent(request.TripId!), "TripId");
            if (!string.IsNullOrWhiteSpace(request.CarrierInvoiceNumber))
                content.Add(new StringContent(request.CarrierInvoiceNumber.Trim()), "CarrierInvoiceNumber");
            if (!string.IsNullOrWhiteSpace(request.PaymentType))
                content.Add(new StringContent(request.PaymentType.Trim()), "PaymentType");
        }
        else
        {
            // Load/trip document uploads carry the DocumentType classification.
            var documentType = op.Kind == AlvysWriteOperationKind.UploadLoadDocument
                ? AlvysLoadDocumentTypes.Canonical(request.DocumentType)
                : AlvysTripDocumentTypes.Canonical(request.DocumentType);
            content.Add(new StringContent(documentType ?? request.DocumentType!.Trim()), "DocumentType");
        }

        return content;
    }

    private static string ResolveEndpoint(
        AlvysWriteOperationDescriptor op, AlvysOperationRequest request, string? apiVersion) =>
        op.Kind switch
        {
            AlvysWriteOperationKind.UploadLoadDocument =>
                AlvysApiRoutes.LoadDocumentUpload(apiVersion, request.LoadNumber!),
            AlvysWriteOperationKind.UploadTripDocument =>
                AlvysApiRoutes.TripDocumentUpload(apiVersion, request.TripId!),
            AlvysWriteOperationKind.CreateCarrierInvoice =>
                AlvysApiRoutes.CarrierInvoice(apiVersion),
            _ => throw new InvalidOperationException($"No upload endpoint mapping for {op.Kind}."),
        };
}

/// <summary>
/// No-op <see cref="IAlvysDocumentUploadClient"/> for unit tests. Returns success with no attachment
/// reference and makes no network call. It fabricates no live document metadata.
/// </summary>
public sealed class NoOpAlvysDocumentUploadClient : IAlvysDocumentUploadClient
{
    public Task<AlvysDocumentUploadResult> UploadAsync(
        AlvysWriteOperationDescriptor op,
        AlvysOperationRequest request,
        AlvysOperationPayload payload,
        CancellationToken ct = default) =>
        Task.FromResult(new AlvysDocumentUploadResult { IsSuccess = true, StatusCode = 200 });
}
