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
        StubHttpMessageHandler handler, CapturingLogger<AlvysClient> logger, string apiVersion = "v1")
        => new(
            new StubHttpClientFactory(handler, new Uri("https://alvys.test/")),
            new StubTokenProvider(),
            Microsoft.Extensions.Options.Options.Create(new AlvysOptions { ApiVersion = apiVersion }),
            logger);

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

    [Theory]
    [InlineData("v1", "/api/p/v1/loads/search")]
    [InlineData("v2.0", "/api/p/v2.0/loads/search")]
    [InlineData("2.0", "/api/p/v2.0/loads/search")]   // normalized — no double "v"
    public async Task SearchLoads_targets_versioned_path(string version, string expectedPath)
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"Items":[]}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>(), version);

        await client.SearchLoadsAsync();

        Assert.Equal(expectedPath, handler.Calls[0].Request.RequestUri?.AbsolutePath);
    }

    [Theory]
    [InlineData("v1", "/api/p/v1/trips/search")]
    [InlineData("v2.0", "/api/p/v2.0/trips/search")]
    public async Task SearchTrips_targets_versioned_path_and_sends_bearer_token(string version, string expectedPath)
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"Page":0,"PageSize":100,"Total":1,"Items":[{"Id":"T1","TripNumber":"500","Status":"In Transit","LoadedMileage":120.5,"Trailer":{"Id":"TR1","EquipmentType":"Reefer"}}]}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>(), version);

        var result = await client.SearchTripsAsync(new TripSearchRequest { TripNumbers = ["500"] });

        var item = Assert.Single(result.Items);
        Assert.Equal("T1", item.Id);
        Assert.Equal("500", item.TripNumber);
        Assert.Equal(120.5m, item.LoadedMileage);
        Assert.Equal("Reefer", item.Trailer?.EquipmentType);
        Assert.Equal(expectedPath, handler.Calls[0].Request.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer test-token", handler.Calls[0].Request.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task SearchTrips_returns_empty_on_server_error_without_throwing()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var logger = new CapturingLogger<AlvysClient>();
        var client = Build(handler, logger);

        var result = await client.SearchTripsAsync(new TripSearchRequest { Status = ["In Transit"] });

        Assert.Empty(result.Items);
        Assert.Contains("429", logger.AllText);
    }

    [Fact]
    public async Task SearchLoads_rejects_non_positive_page_size_before_calling_alvys()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SearchLoadsAsync(new LoadSearchRequest { PageSize = 0 }));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task SearchLoads_rejects_more_than_150_load_numbers()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var client = Build(handler, new CapturingLogger<AlvysClient>());
        var request = new LoadSearchRequest
        {
            LoadNumbers = Enumerable.Range(0, 151).Select(i => i.ToString()).ToList(),
        };

        await Assert.ThrowsAsync<ArgumentException>(() => client.SearchLoadsAsync(request));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task SearchTrips_serializes_only_supplied_filters_in_pascal_case()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"Items":[]}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await client.SearchTripsAsync(new TripSearchRequest { Page = 1, TripNumbers = ["500"] });

        var body = handler.Calls[0].Body;
        Assert.Contains("\"Page\":1", body);
        Assert.Contains("\"TripNumbers\":[\"500\"]", body);
        Assert.DoesNotContain("Status", body);          // null filters are omitted
        Assert.DoesNotContain("PickupDateRange", body);
    }

    [Theory]
    [InlineData("v1", "/api/p/v1/trailers/search")]
    [InlineData("2.0", "/api/p/v2.0/trailers/search")]   // normalized — no double "v"
    public async Task SearchTrailers_maps_response_and_targets_versioned_path_with_bearer(
        string version, string expectedPath)
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"Page":0,"PageSize":100,"Total":1,"Items":[{"Id":"TR1","TrailerNum":"500","Status":"Active","EquipmentType":"Reefer","Fleet":{"Id":"F1","Name":"Main"},"Capacity":{"Pallets":26,"Weight":44000}}]}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>(), version);

        var result = await client.SearchTrailersAsync(new TrailerSearchRequest { TrailerNumber = "500" });

        var item = Assert.Single(result.Items);
        Assert.Equal("TR1", item.Id);
        Assert.Equal("500", item.TrailerNum);
        Assert.Equal("Reefer", item.EquipmentType);
        Assert.Equal("Main", item.Fleet?.Name);
        Assert.Equal(26m, item.Capacity?.Pallets);
        Assert.Equal(44000m, item.Capacity?.Weight);
        Assert.Equal(expectedPath, handler.Calls[0].Request.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer test-token", handler.Calls[0].Request.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task SearchTrailers_serializes_only_supplied_filters_in_pascal_case()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"Items":[]}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await client.SearchTrailersAsync(new TrailerSearchRequest { Page = 2, FleetName = "Main" });

        var body = handler.Calls[0].Body;
        Assert.Contains("\"Page\":2", body);
        Assert.Contains("\"FleetName\":\"Main\"", body);
        Assert.DoesNotContain("TrailerNumber", body);   // null filters are omitted
        Assert.DoesNotContain("VinNumber", body);
    }

    [Fact]
    public async Task SearchTrailers_rejects_non_positive_page_size_before_calling_alvys()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SearchTrailersAsync(new TrailerSearchRequest { PageSize = 0 }));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task SearchTrailers_returns_empty_on_rate_limit_without_throwing()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var logger = new CapturingLogger<AlvysClient>();
        var client = Build(handler, logger);

        var result = await client.SearchTrailersAsync(new TrailerSearchRequest { Status = ["Active"] });

        Assert.Empty(result.Items);
        Assert.Contains("429", logger.AllText);
    }

    [Theory]
    [InlineData("v1", "/api/p/v1/trucks/search")]
    [InlineData("v2.0", "/api/p/v2.0/trucks/search")]
    public async Task SearchTrucks_maps_response_and_targets_versioned_path_with_bearer(
        string version, string expectedPath)
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"Page":0,"PageSize":100,"Total":1,"Items":[{"Id":"TK1","TruckNum":"42","Status":"Active","GrossWeight":33000,"EmptyWeight":17000,"NumberOfAxles":3,"FuelCards":[{"Number":"1234","Type":"Comdata"}]}]}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>(), version);

        var result = await client.SearchTrucksAsync(new TruckSearchRequest { IsActive = true });

        var item = Assert.Single(result.Items);
        Assert.Equal("TK1", item.Id);
        Assert.Equal("42", item.TruckNum);
        Assert.Equal(33000m, item.GrossWeight);
        Assert.Equal(17000m, item.EmptyWeight);
        Assert.Equal(3, item.NumberOfAxles);
        Assert.Equal("Comdata", Assert.Single(item.FuelCards!).Type);
        Assert.Equal(expectedPath, handler.Calls[0].Request.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer test-token", handler.Calls[0].Request.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task SearchTrucks_serializes_only_supplied_filters_in_pascal_case()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"Items":[]}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await client.SearchTrucksAsync(new TruckSearchRequest { Page = 1, IsActive = true, TruckNumber = "42" });

        var body = handler.Calls[0].Body;
        Assert.Contains("\"Page\":1", body);
        Assert.Contains("\"IsActive\":true", body);
        Assert.Contains("\"TruckNumber\":\"42\"", body);
        Assert.DoesNotContain("FleetName", body);   // null filters are omitted
        Assert.DoesNotContain("RegisteredName", body);
    }

    [Fact]
    public async Task SearchTrucks_rejects_non_positive_page_size_before_calling_alvys()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SearchTrucksAsync(new TruckSearchRequest { PageSize = 0 }));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task SearchTrucks_returns_empty_on_server_error_without_throwing()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var logger = new CapturingLogger<AlvysClient>();
        var client = Build(handler, logger);

        var result = await client.SearchTrucksAsync(new TruckSearchRequest { Status = ["Active"] });

        Assert.Empty(result.Items);
        Assert.Contains("500", logger.AllText);
    }
}
