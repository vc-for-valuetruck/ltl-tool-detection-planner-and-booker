using System.Net;
using LtlTool.Api.Features.Integrations.Alvys;
using Xunit;

namespace LtlTool.Api.Tests.Alvys;

public sealed class AlvysClientTests
{
    private sealed class StubTokenProvider : IAlvysTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
            => Task.FromResult("test-token");
    }

    private static AlvysClient Build(
        StubHttpMessageHandler handler, CapturingLogger<AlvysClient> logger)
        => new(new StubHttpClientFactory(handler, new Uri("https://alvys.test/api/")), new StubTokenProvider(), logger);

    [Fact]
    public async Task SearchLoads_parses_paged_response_and_sends_bearer_token()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"Page":0,"PageSize":100,"Total":1,"Items":[{"Id":"L1","LoadNumber":"100","Status":"Open"}]}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        var result = await client.SearchLoadsAsync();

        var item = Assert.Single(result.Items);
        Assert.Equal("L1", item.Id);
        Assert.Equal("100", item.LoadNumber);
        Assert.Equal("Bearer test-token", handler.Calls[0].Request.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task SearchLoads_translates_page_to_zero_based_and_defaults_statuses()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"Items":[]}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await client.SearchLoadsAsync(page: 2);

        var body = handler.Calls[0].Body;
        Assert.Contains("\"Page\":1", body);   // 1-based 2 -> 0-based 1
        Assert.Contains("\"Status\":[", body);  // full status list applied when none supplied
    }

    [Fact]
    public async Task SearchLoads_returns_empty_on_server_error_without_throwing()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var logger = new CapturingLogger<AlvysClient>();
        var client = Build(handler, logger);

        var result = await client.SearchLoadsAsync();

        Assert.Empty(result.Items);
        Assert.Contains("500", logger.AllText);
    }

    [Fact]
    public async Task GetLoadByNumber_returns_null_on_server_error()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        Assert.Null(await client.GetLoadByNumberAsync("999"));
    }

    [Fact]
    public async Task GetLoadByNumber_returns_first_match()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"Items":[{"Id":"L9","LoadNumber":"999","Status":"Delivered"}]}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        var load = await client.GetLoadByNumberAsync("999");

        Assert.NotNull(load);
        Assert.Equal("999", load!.LoadNumber);
    }
}
