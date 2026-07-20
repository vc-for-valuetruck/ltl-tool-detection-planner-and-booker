using System.Net;
using LtlTool.Api.Features.Integrations.Alvys;
using Xunit;

namespace LtlTool.Api.Tests.Alvys;

/// <summary>
/// Tests the per-acting-user internal-API session-token provider (decision #10). Invariants: a token
/// is acquired and cached per acting user; different acting users never share a token;
/// <see cref="AlvysInternalTokenProvider.InvalidateToken"/> forces re-acquisition; the provider
/// refuses to reach the network when the internal API is disabled/misconfigured or the acting user is
/// missing; and acquisition failures never log the response body (which can echo auth material).
/// </summary>
public sealed class AlvysInternalTokenProviderTests
{
    private const string Secret = "internal-session-token-must-never-be-logged";

    private static AlvysInternalApiOptions Enabled() => new()
    {
        Enabled = true,
        BaseUrl = "https://internal.alvys.example.com",
    };

    private static AlvysInternalTokenProvider Build(
        StubHttpMessageHandler handler,
        CapturingLogger<AlvysInternalTokenProvider> logger,
        AlvysInternalApiOptions options)
        => new(new StubHttpClientFactory(handler),
            Microsoft.Extensions.Options.Options.Create(options), logger);

    private static HttpResponseMessage TokenOk(string token = "abc123", int expiresIn = 3600) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"accessToken":"{{token}}","expiresIn":{{expiresIn}}}"""),
        };

    [Fact]
    public async Task Acquires_and_caches_token_per_acting_user()
    {
        var handler = new StubHttpMessageHandler((_, _) => TokenOk());
        var provider = Build(handler, new CapturingLogger<AlvysInternalTokenProvider>(), Enabled());

        var first = await provider.GetSessionTokenAsync("dispatcher-1");
        var second = await provider.GetSessionTokenAsync("dispatcher-1");

        Assert.Equal("abc123", first);
        Assert.Equal("abc123", second);
        Assert.Single(handler.Calls); // cached — one acquisition for the same acting user.
    }

    [Fact]
    public async Task Different_acting_users_do_not_share_a_token()
    {
        var handler = new StubHttpMessageHandler((_, _) => TokenOk());
        var provider = Build(handler, new CapturingLogger<AlvysInternalTokenProvider>(), Enabled());

        await provider.GetSessionTokenAsync("dispatcher-1");
        await provider.GetSessionTokenAsync("dispatcher-2");

        Assert.Equal(2, handler.Calls.Count); // never shares one dispatcher's token with another.
    }

    [Fact]
    public async Task Invalidate_forces_reacquisition()
    {
        var handler = new StubHttpMessageHandler((_, _) => TokenOk());
        var provider = Build(handler, new CapturingLogger<AlvysInternalTokenProvider>(), Enabled());

        await provider.GetSessionTokenAsync("dispatcher-1");
        provider.InvalidateToken("dispatcher-1");
        await provider.GetSessionTokenAsync("dispatcher-1");

        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task Throws_when_internal_api_disabled()
    {
        var handler = new StubHttpMessageHandler((_, _) => TokenOk());
        var provider = Build(handler, new CapturingLogger<AlvysInternalTokenProvider>(),
            new AlvysInternalApiOptions { Enabled = false, BaseUrl = "https://x" });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetSessionTokenAsync("dispatcher-1"));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Throws_when_base_url_missing()
    {
        var handler = new StubHttpMessageHandler((_, _) => TokenOk());
        var provider = Build(handler, new CapturingLogger<AlvysInternalTokenProvider>(),
            new AlvysInternalApiOptions { Enabled = true, BaseUrl = "" });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetSessionTokenAsync("dispatcher-1"));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Throws_when_acting_user_missing()
    {
        var handler = new StubHttpMessageHandler((_, _) => TokenOk());
        var provider = Build(handler, new CapturingLogger<AlvysInternalTokenProvider>(), Enabled());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetSessionTokenAsync("   "));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Failed_acquisition_does_not_log_body()
    {
        // Body deliberately echoes the secret to prove it is never logged.
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent($$"""{"error":"unauthorized","echo":"{{Secret}}"}"""),
        });
        var logger = new CapturingLogger<AlvysInternalTokenProvider>();
        var provider = Build(handler, logger, Enabled());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetSessionTokenAsync("dispatcher-1"));

        Assert.DoesNotContain(Secret, logger.AllText);
        Assert.Contains("401", logger.AllText);
    }
}
