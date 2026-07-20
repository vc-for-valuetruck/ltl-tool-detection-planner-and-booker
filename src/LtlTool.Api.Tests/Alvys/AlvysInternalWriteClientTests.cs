using System.Net;
using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Integrations.Alvys.Writeback;
using Xunit;

namespace LtlTool.Api.Tests.Alvys;

/// <summary>
/// Tests the internal-API write client against a fake handler — no network. The critical, mandated
/// behaviour (decision #10) is the <c>token_expired</c> path: a single re-authentication retry, then
/// an honest surfaced failure. The client also refuses to dispatch when the internal API is
/// disabled/unconfigured or the acting user is missing, attaches the session token as a bearer
/// header, and never logs response bodies.
/// </summary>
public sealed class AlvysInternalWriteClientTests
{
    private static readonly AlvysWriteOperationDescriptor SetRefs =
        AlvysWriteOperationRegistry.Find("set-trip-references")!;

    private static AlvysInternalApiOptions Enabled() => new()
    {
        Enabled = true,
        BaseUrl = "https://internal.alvys.example.com",
    };

    private static AlvysOperationRequest Request() => new()
    {
        TripId = "T-1",
        ActingUserId = "dispatcher-1",
        LtlReference = true,
        MainLoadId = "ML-9000",
    };

    private static AlvysOperationPayload Payload() => new()
    {
        OperationCode = SetRefs.Code,
        TargetDescription = "PATCH internal trip T-1 references",
        RequiresEtag = false,
        EtagSupplied = false,
        Body = new Dictionary<string, object?> { ["LTL"] = "true", ["MainLoadId"] = "ML-9000" },
    };

    private static AlvysHttpInternalWriteClient Build(
        StubHttpMessageHandler handler, FakeInternalTokenProvider tokens,
        AlvysInternalApiOptions? options = null,
        CapturingLogger<AlvysHttpInternalWriteClient>? logger = null)
        => new(new StubHttpClientFactory(handler), tokens,
            Microsoft.Extensions.Options.Options.Create(options ?? Enabled()),
            logger ?? new CapturingLogger<AlvysHttpInternalWriteClient>());

    [Fact]
    public async Task Success_dispatches_once_with_bearer_token()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var tokens = new FakeInternalTokenProvider("session-1");
        var client = Build(handler, tokens);

        var result = await client.ExecuteAsync(SetRefs, Request(), Payload());

        Assert.True(result.IsSuccess);
        Assert.Single(handler.Calls);
        var sent = handler.Calls[0].Request;
        Assert.Equal(HttpMethod.Patch, sent.Method);
        Assert.EndsWith("/internal/trips/T-1/references", sent.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", sent.Headers.Authorization!.Scheme);
        Assert.Equal("session-1", sent.Headers.Authorization.Parameter);
        Assert.Equal(1, tokens.AcquireCount);
    }

    [Fact]
    public async Task Token_expired_reauthenticates_once_then_succeeds()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":"token_expired"}"""),
            },
            new HttpResponseMessage(HttpStatusCode.OK),
        ]);
        var handler = new StubHttpMessageHandler((_, _) => responses.Dequeue());
        var tokens = new FakeInternalTokenProvider("session-1", "session-2");
        var client = Build(handler, tokens);

        var result = await client.ExecuteAsync(SetRefs, Request(), Payload());

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.Calls.Count);         // original + one retry
        Assert.Equal(1, tokens.InvalidateCount);      // exactly one re-auth
        Assert.Equal(2, tokens.AcquireCount);
        // The retry used the freshly re-acquired token.
        Assert.Equal("session-2", handler.Calls[1].Request.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task Token_expired_twice_surfaces_honest_failure_after_single_retry()
    {
        // Both attempts report token_expired: the client must retry exactly once, then fail honestly
        // — never loop. This is the mandated token_expired regression test (decision #10).
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":"token_expired"}"""),
        });
        var tokens = new FakeInternalTokenProvider("session-1", "session-2");
        var client = Build(handler, tokens);

        var result = await client.ExecuteAsync(SetRefs, Request(), Payload());

        Assert.False(result.IsSuccess);
        Assert.Contains("token_expired", result.Error);
        Assert.Equal(2, handler.Calls.Count);    // original + one retry only
        Assert.Equal(1, tokens.InvalidateCount);
    }

    [Fact]
    public async Task Non_token_error_is_not_retried()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage((HttpStatusCode)422)
        {
            Content = new StringContent("""{"error":"validation"}"""),
        });
        var tokens = new FakeInternalTokenProvider("session-1");
        var client = Build(handler, tokens);

        var result = await client.ExecuteAsync(SetRefs, Request(), Payload());

        Assert.False(result.IsSuccess);
        Assert.Equal(422, result.StatusCode);
        Assert.Single(handler.Calls);            // no retry for a non-token failure
        Assert.Equal(0, tokens.InvalidateCount);
    }

    [Fact]
    public async Task Refuses_when_disabled()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var tokens = new FakeInternalTokenProvider("session-1");
        var client = Build(handler, tokens, new AlvysInternalApiOptions { Enabled = false, BaseUrl = "https://x" });

        var result = await client.ExecuteAsync(SetRefs, Request(), Payload());

        Assert.False(result.IsSuccess);
        Assert.Equal("internal_api_disabled", result.Error);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Refuses_when_acting_user_missing()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var tokens = new FakeInternalTokenProvider("session-1");
        var client = Build(handler, tokens);

        var request = Request();
        request.ActingUserId = null;
        var result = await client.ExecuteAsync(SetRefs, request, Payload());

        Assert.False(result.IsSuccess);
        Assert.Equal("acting_user_missing", result.Error);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Failure_does_not_log_response_body()
    {
        const string secret = "internal-echo-should-never-be-logged";
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage((HttpStatusCode)500)
        {
            Content = new StringContent($$"""{"echo":"{{secret}}"}"""),
        });
        var logger = new CapturingLogger<AlvysHttpInternalWriteClient>();
        var tokens = new FakeInternalTokenProvider("session-1");
        var client = Build(handler, tokens, logger: logger);

        await client.ExecuteAsync(SetRefs, Request(), Payload());

        Assert.DoesNotContain(secret, logger.AllText);
        Assert.Contains("500", logger.AllText);
    }

    private sealed class FakeInternalTokenProvider(params string[] tokens) : IAlvysInternalTokenProvider
    {
        private readonly Queue<string> _tokens = new(tokens);
        public int AcquireCount { get; private set; }
        public int InvalidateCount { get; private set; }

        public Task<string> GetSessionTokenAsync(string actingUserId, CancellationToken ct = default)
        {
            AcquireCount++;
            // Reuse the last token if the queue is exhausted so a test that under-provisions still runs.
            var token = _tokens.Count > 0 ? _tokens.Dequeue() : "session-fallback";
            return Task.FromResult(token);
        }

        public void InvalidateToken(string actingUserId) => InvalidateCount++;
    }
}
