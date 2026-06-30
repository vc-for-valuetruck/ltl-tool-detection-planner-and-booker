using System.Net;
using LtlTool.Api.Features.Integrations.Alvys;
using Microsoft.Extensions.Options;
using Xunit;

namespace LtlTool.Api.Tests.Alvys;

public sealed class AlvysTokenProviderTests
{
    private const string Secret = "super-secret-value-should-never-be-logged";

    private static AlvysTokenProvider Build(
        StubHttpMessageHandler handler, CapturingLogger<AlvysTokenProvider> logger, AlvysOptions options)
        => new(new StubHttpClientFactory(handler), Microsoft.Extensions.Options.Options.Create(options), logger);

    [Fact]
    public async Task Acquires_and_caches_token()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"access_token":"abc123","expires_in":3600,"token_type":"Bearer"}"""),
        });
        var logger = new CapturingLogger<AlvysTokenProvider>();
        var provider = Build(handler, logger, new AlvysOptions { ClientId = "cid", ClientSecret = Secret });

        var first = await provider.GetAccessTokenAsync();
        var second = await provider.GetAccessTokenAsync();

        Assert.Equal("abc123", first);
        Assert.Equal("abc123", second);
        Assert.Single(handler.Calls); // cached — only one network call.
    }

    [Fact]
    public async Task Throws_when_credentials_missing()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var logger = new CapturingLogger<AlvysTokenProvider>();
        var provider = Build(handler, logger, new AlvysOptions()); // no creds

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAccessTokenAsync());
        Assert.Empty(handler.Calls); // never attempts a network call without credentials.
    }

    [Fact]
    public async Task Failed_token_request_does_not_log_secret()
    {
        // Body deliberately echoes the secret to prove it is never logged.
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent($$"""{"error":"invalid_client","sent_secret":"{{Secret}}"}"""),
        });
        var logger = new CapturingLogger<AlvysTokenProvider>();
        var provider = Build(handler, logger, new AlvysOptions { ClientId = "cid", ClientSecret = Secret });

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAccessTokenAsync());

        Assert.DoesNotContain(Secret, logger.AllText);
        Assert.Contains("401", logger.AllText);
    }
}
