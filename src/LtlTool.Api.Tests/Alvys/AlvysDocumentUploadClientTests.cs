using System.Net;
using System.Text.Json;
using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Integrations.Alvys.Writeback;
using Xunit;

namespace LtlTool.Api.Tests.Alvys;

/// <summary>
/// Verifies the multipart document-upload client. The load-bearing safety property: the upload
/// authenticates with the <b>Public-API client-credentials</b> bearer token (<see cref="IAlvysTokenProvider"/>),
/// never an internal-API session token — the client does not even depend on
/// <see cref="IAlvysInternalTokenProvider"/>. It also posts multipart/form-data with the file part and the
/// DocumentType, and surfaces the returned attachment reference without inventing metadata.
/// </summary>
public sealed class AlvysDocumentUploadClientTests
{
    private sealed class FixedTokenProvider(string token) : IAlvysTokenProvider
    {
        public int Calls { get; private set; }
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(token);
        }
    }

    private static AlvysOperationRequest UploadRequest() => new()
    {
        LoadNumber = "L-1001",
        DocumentType = "POD",
        FileBytes = [1, 2, 3, 4],
        FileName = "pod.pdf",
        ContentType = "application/pdf",
    };

    private static AlvysOperationPayload Payload() => new()
    {
        OperationCode = "upload-load-document",
        TargetDescription = "POST /loads/L-1001/document (multipart)",
        Body = new Dictionary<string, object?>(),
    };

    [Fact]
    public async Task Upload_uses_public_client_credentials_token_and_posts_multipart()
    {
        var descriptor = AlvysWriteOperationRegistry.Find("upload-load-document")!;
        var tokenProvider = new FixedTokenProvider("public-cc-token-xyz");
        string? sentAuth = null;
        string? sentContentType = null;
        string capturedBody = "";

        var handler = new StubHttpMessageHandler((request, body) =>
        {
            sentAuth = request.Headers.Authorization?.ToString();
            sentContentType = request.Content?.Headers.ContentType?.MediaType;
            capturedBody = body;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { id = "att-77", attachmentPath = "/docs/att-77.pdf", attachmentType = "POD" })),
            };
        });

        var client = new AlvysHttpDocumentUploadClient(
            new StubHttpClientFactory(handler, new Uri("https://sandbox.alvys.test/")),
            tokenProvider,
            Microsoft.Extensions.Options.Options.Create(new AlvysWriteOptions { SandboxBaseUrl = "https://sandbox.alvys.test" }),
            Microsoft.Extensions.Options.Options.Create(new AlvysOptions()),
            new CapturingLogger<AlvysHttpDocumentUploadClient>());

        var result = await client.UploadAsync(descriptor, UploadRequest(), Payload());

        Assert.True(result.IsSuccess);
        Assert.Equal("att-77", result.AttachmentId);
        Assert.Equal("/docs/att-77.pdf", result.AttachmentPath);
        // The Public-API token was requested and forwarded as the bearer — not an internal session token.
        Assert.Equal(1, tokenProvider.Calls);
        Assert.Equal("Bearer public-cc-token-xyz", sentAuth);
        Assert.Equal("multipart/form-data", sentContentType);
        Assert.Contains("DocumentType", capturedBody);
    }

    [Fact]
    public async Task Upload_failure_surfaces_status_without_inventing_attachment()
    {
        var descriptor = AlvysWriteOperationRegistry.Find("upload-load-document")!;
        var handler = new StubHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.BadGateway));

        var client = new AlvysHttpDocumentUploadClient(
            new StubHttpClientFactory(handler, new Uri("https://sandbox.alvys.test/")),
            new FixedTokenProvider("t"),
            Microsoft.Extensions.Options.Options.Create(new AlvysWriteOptions { SandboxBaseUrl = "https://sandbox.alvys.test" }),
            Microsoft.Extensions.Options.Options.Create(new AlvysOptions()),
            new CapturingLogger<AlvysHttpDocumentUploadClient>());

        var result = await client.UploadAsync(descriptor, UploadRequest(), Payload());

        Assert.False(result.IsSuccess);
        Assert.Equal(502, result.StatusCode);
        Assert.Null(result.AttachmentId);
        Assert.Null(result.AttachmentPath);
    }
}
