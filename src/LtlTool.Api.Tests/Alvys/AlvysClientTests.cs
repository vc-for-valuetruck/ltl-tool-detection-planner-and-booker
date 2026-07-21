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
    public async Task SearchLoads_defaults_statuses_when_bare_request_has_no_conditional_filter()
    {
        // Regression: a bare paged sweep (the exceptions/billing worklist path) carries no
        // conditional filter. Alvys rejects that with an empty result, so the primary overload
        // must apply the full-status default — otherwise the live Exceptions tab shows nothing.
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"Items":[]}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await client.SearchLoadsAsync(new LoadSearchRequest { PageSize = 100 });

        var body = handler.Calls[0].Body;
        Assert.Contains("\"Status\":[", body);        // full status list applied
        Assert.Contains("\"In Transit\"", body);      // includes the in-transit late/stuck loads
    }

    [Fact]
    public async Task SearchLoads_does_not_add_status_when_load_numbers_filter_is_present()
    {
        // GetLoadByNumberAsync posts a LoadNumbers lookup, which already satisfies Alvys'
        // conditional-filter requirement. It must not be widened to an all-status sweep.
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"Items":[]}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await client.SearchLoadsAsync(new LoadSearchRequest { PageSize = 1, LoadNumbers = ["100"] });

        var body = handler.Calls[0].Body;
        Assert.Contains("\"LoadNumbers\":[\"100\"]", body);
        Assert.DoesNotContain("Status", body);        // no spurious status default
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
                // LoadedMileage empirical shape (verified 2026-07-18 against va336 via MCP):
                // { Distance: {Value, UnitOfMeasure}, Source, ProfileName }
                // TripValue: { Amount, Currency } (Currency = 840 = USD).
                """{"Page":0,"PageSize":100,"Total":1,"Items":[{"Id":"T1","TripNumber":"500","Status":"In Transit","LoadedMileage":{"Distance":{"Value":120.5,"UnitOfMeasure":"Miles"},"Source":"Engine","ProfileName":"PCMiler"},"TripValue":{"Amount":847.5,"Currency":840},"Trailer":{"Id":"TR1","EquipmentType":"Reefer"}}]}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>(), version);

        var result = await client.SearchTripsAsync(new TripSearchRequest { TripNumbers = ["500"] });

        var item = Assert.Single(result.Items);
        Assert.Equal("T1", item.Id);
        Assert.Equal("500", item.TripNumber);
        // Empirical shape: LoadedMileage.Distance.Value carries the numeric miles.
        // The .Value convenience accessor on AlvysDistanceMeasurement flattens the nesting.
        Assert.Equal(120.5m, item.LoadedMileage?.Value);
        Assert.Equal("Miles", item.LoadedMileage?.Distance?.UnitOfMeasure);
        Assert.Equal("Engine", item.LoadedMileage?.Source);
        Assert.Equal(847.5m, item.TripValue?.Amount);
        // Trip endpoint returns numeric ISO-4217 (840); AlvysMoneyCurrencyConverter normalises to alpha "USD".
        Assert.Equal("USD", item.TripValue?.Currency);
        Assert.Equal("Reefer", item.Trailer?.EquipmentType);
        Assert.Equal(expectedPath, handler.Calls[0].Request.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer test-token", handler.Calls[0].Request.Headers.Authorization?.ToString());
    }

    [Theory]
    [InlineData("v1", "/api/p/v1/loads/100/documents")]
    [InlineData("2.0", "/api/p/v2.0/loads/100/documents")]   // normalized — no double "v"
    public async Task ListLoadDocuments_maps_bare_array_and_targets_versioned_path_with_bearer(
        string version, string expectedPath)
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """[{"id":"DOC1","AttachmentPath":"loads/100/rate-con.pdf","AttachmentType":"RateConfirmation","AttachmentSize":20480,"UploadedAt":"2026-01-02T03:04:05Z","ParentId":"L1","ParentType":"Load","UploadedBy":"jane","DownloadUrl":"https://files.alvys.test/abc","ExpiresAt":"2026-01-02T04:04:05Z"}]"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>(), version);

        var result = await client.ListLoadDocumentsAsync("100");

        var item = Assert.Single(result);
        Assert.Equal("DOC1", item.Id);
        Assert.Equal("loads/100/rate-con.pdf", item.AttachmentPath);
        Assert.Equal("RateConfirmation", item.AttachmentType);
        Assert.Equal(20480L, item.AttachmentSize);
        Assert.Equal("L1", item.ParentId);
        Assert.Equal("https://files.alvys.test/abc", item.DownloadUrl);
        Assert.Equal(HttpMethod.Get, handler.Calls[0].Request.Method);
        Assert.Equal(expectedPath, handler.Calls[0].Request.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer test-token", handler.Calls[0].Request.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task ListLoadDocuments_url_encodes_load_number_in_path()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]"),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await client.ListLoadDocumentsAsync("VT 100/A");

        // AbsolutePath is decoded; AbsoluteUri preserves the percent-encoding.
        Assert.Contains("/api/p/v1/loads/VT%20100%2FA/documents", handler.Calls[0].Request.RequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task ListLoadDocuments_returns_empty_on_rate_limit_without_throwing()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var logger = new CapturingLogger<AlvysClient>();
        var client = Build(handler, logger);

        var result = await client.ListLoadDocumentsAsync("100");

        Assert.Empty(result);
        Assert.Contains("429", logger.AllText);
    }

    [Theory]
    [InlineData("v1", "/api/p/v1/loads/100/notes")]
    [InlineData("2.0", "/api/p/v2.0/loads/100/notes")]   // normalized — no double "v"
    public async Task ListLoadNotes_maps_bare_array_and_targets_versioned_path_with_bearer(
        string version, string expectedPath)
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """[{"Id":"N1","Description":"Detention approved","NoteType":"Operations","CreatedAt":"2026-01-02T03:04:05Z","CreatedBy":"Jane Doe","CreatedById":"U1"}]"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>(), version);

        var result = await client.ListLoadNotesAsync("100");

        var item = Assert.Single(result);
        Assert.Equal("N1", item.Id);
        Assert.Equal("Detention approved", item.Description);
        Assert.Equal("Operations", item.NoteType);
        Assert.Equal("Jane Doe", item.CreatedBy);
        Assert.Equal("U1", item.CreatedById);
        Assert.Equal(HttpMethod.Get, handler.Calls[0].Request.Method);
        Assert.Equal(expectedPath, handler.Calls[0].Request.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer test-token", handler.Calls[0].Request.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task ListLoadNotes_returns_empty_on_server_error_without_throwing()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var logger = new CapturingLogger<AlvysClient>();
        var client = Build(handler, logger);

        var result = await client.ListLoadNotesAsync("100");

        Assert.Empty(result);
        Assert.Contains("500", logger.AllText);
    }

    [Fact]
    public async Task ListLoadNotes_returns_empty_on_not_found_without_logging_error()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        var logger = new CapturingLogger<AlvysClient>();
        var client = Build(handler, logger);

        var result = await client.ListLoadNotesAsync("999");

        Assert.Empty(result);
        Assert.Empty(logger.Messages);
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

    [Theory]
    [InlineData("v1", "/api/p/v1/dispatchpreferences/search")]
    [InlineData("2.0", "/api/p/v2.0/dispatchpreferences/search")]   // normalized — no double "v"
    public async Task SearchDispatchPreferences_maps_bare_array_and_targets_versioned_path_with_bearer(
        string version, string expectedPath)
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """[{"UpdatedAt":"2026-01-02T03:04:05Z","DispatcherId":"D1","Driver1Id":"DR1","TruckId":"TK1","TrailerId":"TR1"}]"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>(), version);

        var result = await client.SearchDispatchPreferencesAsync(
            new DispatchPreferenceSearchRequest { DispatcherIds = ["D1"] });

        var item = Assert.Single(result);
        Assert.Equal("D1", item.DispatcherId);
        Assert.Equal("DR1", item.Driver1Id);
        Assert.Equal("TK1", item.TruckId);
        Assert.Equal("TR1", item.TrailerId);
        Assert.Equal(expectedPath, handler.Calls[0].Request.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer test-token", handler.Calls[0].Request.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task SearchDispatchPreferences_serializes_only_supplied_filters_in_pascal_case()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]"),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await client.SearchDispatchPreferencesAsync(new DispatchPreferenceSearchRequest { TruckIds = ["TK1"] });

        var body = handler.Calls[0].Body;
        Assert.Contains("\"TruckIds\":[\"TK1\"]", body);
        Assert.DoesNotContain("DispatcherIds", body);   // null filters are omitted
        Assert.DoesNotContain("UpdatedAtStart", body);
    }

    [Fact]
    public async Task SearchDispatchPreferences_returns_empty_on_rate_limit_without_throwing()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var logger = new CapturingLogger<AlvysClient>();
        var client = Build(handler, logger);

        var result = await client.SearchDispatchPreferencesAsync(
            new DispatchPreferenceSearchRequest { DriverIds = ["DR1"] });

        Assert.Empty(result);
        Assert.Contains("429", logger.AllText);
    }

    [Theory]
    [InlineData("v1", "/api/p/v1/locations/search")]
    [InlineData("2.0", "/api/p/v2.0/locations/search")]   // normalized — no double "v"
    public async Task SearchLocations_maps_response_and_targets_versioned_path_with_bearer(
        string version, string expectedPath)
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"Page":0,"PageSize":100,"Total":1,"Facets":null,"Aggregations":null,"Items":[{"Id":"LOC1","Name":"Dallas Hub","Type":"Terminal","Status":"Active","PhysicalAddress":{"Street":"1 Main","City":"Dallas","State":"TX","ZipCode":"75201"},"Email":["ops@vt.com"],"Notes":[{"id":"N1","Description":"hub","NoteType":"General","User":"jane"}]}]}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>(), version);

        var result = await client.SearchLocationsAsync(new LocationSearchRequest { Status = ["Active"] });

        var item = Assert.Single(result.Items);
        Assert.Equal("LOC1", item.Id);
        Assert.Equal("Dallas Hub", item.Name);
        Assert.Equal("75201", item.PhysicalAddress?.ZipCode);
        Assert.Equal("ops@vt.com", Assert.Single(item.Email!));
        Assert.Equal("hub", Assert.Single(item.Notes!).Description);
        Assert.Equal(expectedPath, handler.Calls[0].Request.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer test-token", handler.Calls[0].Request.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task SearchLocations_rejects_non_positive_page_size_before_calling_alvys()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SearchLocationsAsync(new LocationSearchRequest { PageSize = 0 }));
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData("v1", "/api/p/v1/drivers/search")]
    [InlineData("2.0", "/api/p/v2.0/drivers/search")]
    public async Task SearchDrivers_maps_response_and_targets_versioned_path_with_bearer(
        string version, string expectedPath)
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"Page":0,"PageSize":100,"Total":1,"Items":[{"Id":"DR1","Name":"Sam Driver","EmployeeId":"E9","Status":"OFF DUTY","IsActive":true,"Address":{"ZipCode":"75201"},"Fleet":{"Id":"F1","Name":"Main"},"References":[{"Id":"R1","Type":"Tractor","Value":"42"}]}]}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>(), version);

        var result = await client.SearchDriversAsync(new DriverSearchRequest { IsActive = true });

        var item = Assert.Single(result.Items);
        Assert.Equal("DR1", item.Id);
        Assert.Equal("Sam Driver", item.Name);
        Assert.True(item.IsActive);
        Assert.Equal("75201", item.Address?.ZipCode);
        Assert.Equal("Main", item.Fleet?.Name);
        Assert.Equal("42", Assert.Single(item.References!).Value);
        Assert.Equal(expectedPath, handler.Calls[0].Request.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer test-token", handler.Calls[0].Request.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task SearchDrivers_serializes_only_supplied_filters_in_pascal_case()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"Items":[]}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await client.SearchDriversAsync(new DriverSearchRequest { Page = 1, FleetName = "Main" });

        var body = handler.Calls[0].Body;
        Assert.Contains("\"Page\":1", body);
        Assert.Contains("\"FleetName\":\"Main\"", body);
        Assert.DoesNotContain("EmployeeId", body);   // null filters are omitted
        Assert.DoesNotContain("IsActive", body);
    }

    [Fact]
    public async Task SearchDrivers_rejects_non_positive_page_size_before_calling_alvys()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SearchDriversAsync(new DriverSearchRequest { PageSize = 0 }));
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData("v1", "/api/p/v1/customers/search")]
    [InlineData("2.0", "/api/p/v2.0/customers/search")]
    public async Task SearchCustomers_maps_response_and_targets_versioned_path_with_bearer(
        string version, string expectedPath)
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"Page":0,"PageSize":100,"Total":1,"Items":[{"Id":"C1","Name":"Acme","Type":"Customer","Status":"Active","BillingAddress":{"ZipCode":"60601"},"InvoicingInformation":{"PaymentTermsInDays":30,"PaymentType":"Net"},"Contacts":[{"Id":"K1","Name":"Pat","Title":"AP"}],"SalesAgentId":"SA1"}]}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>(), version);

        var result = await client.SearchCustomersAsync(new CustomerSearchRequest { Statuses = ["Active"] });

        var item = Assert.Single(result.Items);
        Assert.Equal("C1", item.Id);
        Assert.Equal("Acme", item.Name);
        Assert.Equal("60601", item.BillingAddress?.ZipCode);
        Assert.Equal(30, item.InvoicingInformation?.PaymentTermsInDays);
        Assert.Equal("Pat", Assert.Single(item.Contacts!).Name);
        Assert.Equal("SA1", item.SalesAgentId);
        Assert.Equal(expectedPath, handler.Calls[0].Request.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer test-token", handler.Calls[0].Request.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task SearchCustomers_serializes_statuses_and_page_in_pascal_case()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"Items":[]}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await client.SearchCustomersAsync(new CustomerSearchRequest { Page = 2, Statuses = ["Active"] });

        var body = handler.Calls[0].Body;
        Assert.Contains("\"Page\":2", body);
        Assert.Contains("\"Statuses\":[\"Active\"]", body);
        Assert.DoesNotContain("CreatedDateRange", body);   // null filter omitted
    }

    [Fact]
    public async Task SearchCustomers_rejects_non_positive_page_size_before_calling_alvys()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SearchCustomersAsync(new CustomerSearchRequest { PageSize = 0, Statuses = ["Active"] }));
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData("v1", "/api/p/v1/users/search")]
    [InlineData("2.0", "/api/p/v2.0/users/search")]
    public async Task SearchUsers_maps_response_and_targets_versioned_path_with_bearer(
        string version, string expectedPath)
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"Page":0,"PageSize":100,"Total":1,"Items":[{"Id":"U1","UserName":"jdoe","Name":"Jane Doe","Role":"Dispatcher","Status":"Active","CompanyCode":"VT","Permissions":["loads.read","trips.read"]}]}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>(), version);

        var result = await client.SearchUsersAsync(new UserSearchRequest { Keyword = "jane" });

        var item = Assert.Single(result.Items);
        Assert.Equal("U1", item.Id);
        Assert.Equal("jdoe", item.UserName);
        Assert.Equal("Dispatcher", item.Role);
        Assert.Equal("Active", item.Status);
        Assert.Equal(2, item.Permissions?.Count);
        Assert.Equal(expectedPath, handler.Calls[0].Request.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer test-token", handler.Calls[0].Request.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task SearchUsers_serializes_only_supplied_filters_in_pascal_case()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"Items":[]}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await client.SearchUsersAsync(new UserSearchRequest { Page = 1 });

        var body = handler.Calls[0].Body;
        Assert.Contains("\"Page\":1", body);
        Assert.DoesNotContain("Keyword", body);   // null filter omitted
    }

    [Fact]
    public async Task SearchUsers_rejects_non_positive_page_size_before_calling_alvys()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SearchUsersAsync(new UserSearchRequest { PageSize = 0 }));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task SearchUsers_returns_empty_on_server_error_without_throwing()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var logger = new CapturingLogger<AlvysClient>();
        var client = Build(handler, logger);

        var result = await client.SearchUsersAsync(new UserSearchRequest { Keyword = "x" });

        Assert.Empty(result.Items);
        Assert.Contains("500", logger.AllText);
    }

    [Theory]
    [InlineData("v1", "/api/p/v1/tenders/search")]
    [InlineData("2.0", "/api/p/v2.0/tenders/search")]   // normalized — no double "v"
    public async Task SearchTenders_maps_response_and_targets_versioned_path_with_bearer(
        string version, string expectedPath)
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"Page":0,"PageSize":100,"Total":1,"Facets":null,"Aggregations":null,"Items":[{"Id":"TEN1","CompanyCode":"VT","Status":"Offered","LoadNumber":"100","SCAC":"VTUK","Weight":12000,"WeightUnitCode":"L","QtyPallets":12,"Rate":1850.50,"Equipment":{"Number":"1","Length":53,"Type":"V"},"Entities":[{"Type":"ShipFrom","Name":"Acme","City":"Dallas","State":"TX","PostalCode":"75201"}],"ExpirationDate":{"DateTime":"2026-02-01T12:00:00Z","TimeZoneCode":"America/Chicago"},"Stops":[{"StopId":"S1","Type":"Pickup","SequenceNumber":1,"ScheduledArrivalStart":{"DateTime":"2026-02-02T08:00:00Z"},"Orders":[{"Quantity":10,"Weight":5000,"PoNumber":"PO9"}],"References":[{"Id":"R1","Qualifier":"BM","Description":"BOL"}]}],"References":[{"Id":"T1","Qualifier":"SI","Description":"ship"}]}]}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>(), version);

        var result = await client.SearchTendersAsync(
            new TenderSearchRequest { Filter = new TenderSearchFilter { Status = ["Offered"] } });

        var item = Assert.Single(result.Items);
        Assert.Equal("TEN1", item.Id);
        Assert.Equal("Offered", item.Status);
        Assert.Equal("VTUK", item.SCAC);
        Assert.Equal(12, item.QtyPallets);
        Assert.Equal(1850.50m, item.Rate);
        Assert.Equal("V", item.Equipment?.Type);
        Assert.Equal("Acme", Assert.Single(item.Entities!).Name);
        Assert.Equal("America/Chicago", item.ExpirationDate?.TimeZoneCode);
        var stop = Assert.Single(item.Stops!);
        Assert.Equal("S1", stop.StopId);
        Assert.Equal("Pickup", stop.Type);
        Assert.Equal("PO9", Assert.Single(stop.Orders!).PoNumber);
        Assert.Equal("BM", Assert.Single(stop.References!).Qualifier);
        Assert.Equal("SI", Assert.Single(item.References!).Qualifier);
        Assert.Equal(expectedPath, handler.Calls[0].Request.RequestUri?.AbsolutePath);
        Assert.Equal(HttpMethod.Post, handler.Calls[0].Request.Method);
        Assert.Equal("Bearer test-token", handler.Calls[0].Request.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task SearchTenders_serializes_only_supplied_nested_filters_in_pascal_case()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"Items":[]}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await client.SearchTendersAsync(new TenderSearchRequest
        {
            Page = 1,
            Sort = new TenderSort { Field = "DateImported", Direction = "Desc" },
            Filter = new TenderSearchFilter { LoadNumber = "100" },
        });

        var body = handler.Calls[0].Body;
        Assert.Contains("\"Page\":1", body);
        Assert.Contains("\"Sort\":{", body);
        Assert.Contains("\"Field\":\"DateImported\"", body);
        Assert.Contains("\"LoadNumber\":\"100\"", body);
        Assert.DoesNotContain("Status", body);            // null filters are omitted
        Assert.DoesNotContain("CreatedAtRange", body);
        Assert.DoesNotContain("ExternalTenderId", body);
    }

    [Fact]
    public async Task SearchTenders_rejects_non_positive_page_size_before_calling_alvys()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SearchTendersAsync(new TenderSearchRequest { PageSize = 0 }));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task SearchTenders_returns_empty_on_rate_limit_without_throwing()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var logger = new CapturingLogger<AlvysClient>();
        var client = Build(handler, logger);

        var result = await client.SearchTendersAsync(
            new TenderSearchRequest { Filter = new TenderSearchFilter { Status = ["Offered"] } });

        Assert.Empty(result.Items);
        Assert.Contains("429", logger.AllText);
    }

    [Theory]
    [InlineData("v1", "/api/p/v1/tenders/T-100")]
    [InlineData("2.0", "/api/p/v2.0/tenders/T-100")]   // normalized — no double "v"
    public async Task GetTenderById_maps_detail_and_targets_versioned_path_with_bearer(
        string version, string expectedPath)
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"Id":"T-100","Status":"Offered","LoadNumber":"100","Equipment":{"Type":"V","Length":53},"Stops":[{"StopId":"S1","Type":"Pickup"}],"Etag":"abc"}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>(), version);

        var tender = await client.GetTenderByIdAsync("T-100");

        Assert.NotNull(tender);
        Assert.Equal("T-100", tender!.Id);
        Assert.Equal("Offered", tender.Status);
        Assert.Equal("V", tender.Equipment?.Type);
        Assert.Equal("S1", Assert.Single(tender.Stops!).StopId);
        Assert.Equal("abc", tender.Etag);
        Assert.Equal(expectedPath, handler.Calls[0].Request.RequestUri?.AbsolutePath);
        Assert.Equal(HttpMethod.Get, handler.Calls[0].Request.Method);
        Assert.Equal("Bearer test-token", handler.Calls[0].Request.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task GetTenderById_url_encodes_the_tender_id()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"Id":"a/b c"}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await client.GetTenderByIdAsync("a/b c");

        // The raw request URI keeps the percent-encoded segment (single path segment).
        Assert.Equal("/api/p/v1/tenders/a%2Fb%20c", handler.Calls[0].Request.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task GetTenderById_returns_null_on_not_found()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        var logger = new CapturingLogger<AlvysClient>();
        var client = Build(handler, logger);

        Assert.Null(await client.GetTenderByIdAsync("missing"));
        Assert.DoesNotContain("failed with HTTP", logger.AllText);   // 404 is not an error
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "400")]
    [InlineData(HttpStatusCode.Unauthorized, "401")]
    [InlineData(HttpStatusCode.Forbidden, "403")]
    [InlineData(HttpStatusCode.TooManyRequests, "429")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task GetTenderById_returns_null_and_logs_status_on_non_success(
        HttpStatusCode status, string expectedCode)
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(status));
        var logger = new CapturingLogger<AlvysClient>();
        var client = Build(handler, logger);

        Assert.Null(await client.GetTenderByIdAsync("T-100"));
        Assert.Contains(expectedCode, logger.AllText);
        Assert.DoesNotContain("test-token", logger.AllText);   // token never logged
    }

    [Theory]
    [InlineData("v1", "/api/p/v1/loads")]
    [InlineData("2.0", "/api/p/v2.0/loads")]   // normalized — no double "v"
    public async Task GetLoad_maps_detail_and_targets_versioned_path_with_bearer(
        string version, string expectedPath)
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"Id":"L1","LoadNumber":"100","OrderNumber":"O-9","Status":"Delivered","PickedUpAt":"2026-01-02T03:04:05Z","DeliveredAt":"2026-01-03T03:04:05Z","Payments":[{"Amount":1850.50,"PaidAt":"2026-01-10T00:00:00Z","Reference":"CHK-1","Method":"ACH"}]}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>(), version);

        var load = await client.GetLoadAsync(new LoadLookup { Id = "L1" });

        Assert.NotNull(load);
        Assert.Equal("L1", load!.Id);
        Assert.Equal("100", load.LoadNumber);
        Assert.Equal("O-9", load.OrderNumber);
        Assert.Equal(1850.50m, Assert.Single(load.Payments!).Amount);
        Assert.Equal("ACH", load.Payments![0].Method);
        Assert.Equal(expectedPath, handler.Calls[0].Request.RequestUri?.AbsolutePath);
        Assert.Equal(HttpMethod.Get, handler.Calls[0].Request.Method);
        Assert.Equal("Bearer test-token", handler.Calls[0].Request.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task GetLoad_sends_lookup_as_url_encoded_query()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"Id":"L1"}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await client.GetLoadAsync(new LoadLookup { LoadNumber = "VT 100/A" });

        Assert.Contains("/api/p/v1/loads?loadNumber=VT%20100%2FA", handler.Calls[0].Request.RequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task GetLoad_rejects_empty_lookup_before_calling_alvys()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetLoadAsync(new LoadLookup()));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task GetLoad_returns_null_on_not_found()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        var logger = new CapturingLogger<AlvysClient>();
        var client = Build(handler, logger);

        Assert.Null(await client.GetLoadAsync(new LoadLookup { Id = "missing" }));
        Assert.DoesNotContain("failed with HTTP", logger.AllText);   // 404 is not an error
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "429")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task GetLoad_returns_null_and_logs_status_on_non_success(
        HttpStatusCode status, string expectedCode)
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(status));
        var logger = new CapturingLogger<AlvysClient>();
        var client = Build(handler, logger);

        Assert.Null(await client.GetLoadAsync(new LoadLookup { Id = "L1" }));
        Assert.Contains(expectedCode, logger.AllText);
        Assert.DoesNotContain("test-token", logger.AllText);   // token never logged
    }

    [Theory]
    [InlineData("v1", "/api/p/v1/trips")]
    [InlineData("2.0", "/api/p/v2.0/trips")]   // normalized — no double "v"
    public async Task GetTrip_maps_detail_and_targets_versioned_path_with_bearer(
        string version, string expectedPath)
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"Id":"T1","TripNumber":"500","Status":"Delivered","LoadNumber":"100","OrderNumber":"O-9","Trailer":{"Id":"TR1","EquipmentType":"Reefer"},"PickedUpAt":"2026-01-02T03:04:05Z","DeliveredAt":"2026-01-03T03:04:05Z","CarrierAssignedAt":"2026-01-01T00:00:00Z","DispatcherId":"D1"}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>(), version);

        var trip = await client.GetTripAsync(new TripLookup { Id = "T1" });

        Assert.NotNull(trip);
        Assert.Equal("T1", trip!.Id);
        Assert.Equal("500", trip.TripNumber);
        Assert.Equal("O-9", trip.OrderNumber);
        Assert.Equal("Reefer", trip.Trailer?.EquipmentType);
        Assert.Equal("D1", trip.DispatcherId);
        Assert.Equal(expectedPath, handler.Calls[0].Request.RequestUri?.AbsolutePath);
        Assert.Equal(HttpMethod.Get, handler.Calls[0].Request.Method);
        Assert.Equal("Bearer test-token", handler.Calls[0].Request.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task GetTrip_maps_carrier_payable_money_fields_distinct_from_itemized_accessorials()
    {
        // Mirrors the real Alvys OpenAPI shape (TripResponseCarrierResponse): AccessorialsDetails
        // is the itemized array, Accessorials is a separate aggregate money total, and TotalPayable
        // is Linehaul + Accessorials. These were previously conflated under one "Accessorials" key.
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "Id": "T1",
                  "Carrier": {
                    "Id": "C1",
                    "CarrierInvoiceNumber": "INV-93842",
                    "Linehaul": { "Amount": 900.00, "Currency": "USD" },
                    "AccessorialsDetails": [ { "Type": "Detention", "Amount": 50.00 } ],
                    "Accessorials": { "Amount": 50.00, "Currency": "USD" },
                    "TotalPayable": { "Amount": 950.00, "Currency": "USD" }
                  }
                }
                """),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        var trip = await client.GetTripAsync(new TripLookup { Id = "T1" });

        Assert.NotNull(trip?.Carrier);
        Assert.Equal("INV-93842", trip!.Carrier!.CarrierInvoiceNumber);
        Assert.Equal(900.00m, trip.Carrier.Linehaul?.Amount);
        Assert.Equal(50.00m, trip.Carrier.Accessorials?.Amount);
        Assert.Equal(950.00m, trip.Carrier.TotalPayable?.Amount);
        Assert.Equal("USD", trip.Carrier.TotalPayable?.Currency);
        Assert.Single(trip.Carrier.AccessorialsDetails ?? []);
        Assert.Equal("Detention", trip.Carrier.AccessorialsDetails?[0].Type);
    }

    [Fact]
    public async Task GetTrip_sends_trip_number_and_include_deleted_as_query()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"Id":"T1"}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await client.GetTripAsync(new TripLookup { TripNumber = "TR 7", IncludeDeleted = true });

        var uri = handler.Calls[0].Request.RequestUri?.AbsoluteUri;
        Assert.Contains("/api/p/v1/trips?tripNumber=TR%207", uri);
        Assert.Contains("includeDeleted=true", uri);
    }

    [Fact]
    public async Task GetTrip_rejects_empty_lookup_before_calling_alvys()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetTripAsync(new TripLookup()));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task GetTrip_returns_null_on_not_found()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        var logger = new CapturingLogger<AlvysClient>();
        var client = Build(handler, logger);

        Assert.Null(await client.GetTripAsync(new TripLookup { Id = "missing" }));
        Assert.DoesNotContain("failed with HTTP", logger.AllText);   // 404 is not an error
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "429")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task GetTrip_returns_null_and_logs_status_on_non_success(
        HttpStatusCode status, string expectedCode)
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(status));
        var logger = new CapturingLogger<AlvysClient>();
        var client = Build(handler, logger);

        Assert.Null(await client.GetTripAsync(new TripLookup { Id = "T1" }));
        Assert.Contains(expectedCode, logger.AllText);
        Assert.DoesNotContain("test-token", logger.AllText);   // token never logged
    }

    [Theory]
    [InlineData("v1", "/api/p/v1/trips/T1/stops")]
    [InlineData("2.0", "/api/p/v2.0/trips/T1/stops")]   // normalized — no double "v"
    public async Task ListTripStops_maps_polymorphic_array_and_preserves_type_discriminator(
        string version, string expectedPath)
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """[{"$type":"appointment","Id":"S1","StopType":"Pickup","Status":"Completed","Address":{"Street":"1 Main","City":"Dallas","State":"TX","ZipCode":"75201"},"Coordinates":{"Latitude":32.7,"Longitude":-96.8},"ArrivedAt":"2026-01-02T08:00:00Z","DepartedAt":"2026-01-02T09:00:00Z","CompanyId":"CO1","CompanyName":"Acme","AppointmentRequested":true,"AppointmentConfirmed":true,"AppointmentDate":"2026-01-02T08:00:00Z","ScheduleType":"FCFS","LoadingType":"Live"},{"$type":"delivery_window","Id":"S2","StopType":"Delivery","StopWindow":{"Begin":"2026-01-03T08:00:00Z","End":"2026-01-03T17:00:00Z"},"ScheduleType":"Appt"},{"$type":"waypoint","Id":"S3","StopWindow":{"Begin":"2026-01-02T20:00:00Z"}}]"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>(), version);

        var stops = await client.ListTripStopsAsync("T1");

        Assert.Equal(3, stops.Count);
        Assert.Equal("appointment", stops[0].Type);
        Assert.Equal("S1", stops[0].Id);
        Assert.Equal("75201", stops[0].Address?.ZipCode);
        Assert.Equal(32.7, stops[0].Coordinates?.Latitude);
        Assert.True(stops[0].AppointmentConfirmed);
        Assert.Equal("delivery_window", stops[1].Type);
        Assert.Equal("2026-01-03T17:00:00Z", stops[1].StopWindow?.End?.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        Assert.Equal("waypoint", stops[2].Type);
        Assert.Equal(expectedPath, handler.Calls[0].Request.RequestUri?.AbsolutePath);
        Assert.Equal(HttpMethod.Get, handler.Calls[0].Request.Method);
        Assert.Equal("Bearer test-token", handler.Calls[0].Request.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task ListTripStops_url_encodes_trip_id_in_path()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]"),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await client.ListTripStopsAsync("a/b c");

        Assert.Contains("/api/p/v1/trips/a%2Fb%20c/stops", handler.Calls[0].Request.RequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task ListTripStops_returns_empty_on_not_found_without_logging_error()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        var logger = new CapturingLogger<AlvysClient>();
        var client = Build(handler, logger);

        Assert.Empty(await client.ListTripStopsAsync("999"));
        Assert.Empty(logger.Messages);
    }

    [Fact]
    public async Task ListTripStops_returns_empty_on_server_error_without_throwing()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var logger = new CapturingLogger<AlvysClient>();
        var client = Build(handler, logger);

        Assert.Empty(await client.ListTripStopsAsync("T1"));
        Assert.Contains("500", logger.AllText);
        Assert.DoesNotContain("test-token", logger.AllText);   // token never logged
    }

    // ----- Invoices ---------------------------------------------------------

    [Theory]
    [InlineData("v1", "/api/p/v1/invoices/search")]
    [InlineData("2.0", "/api/p/v2.0/invoices/search")]   // normalized — no double "v"
    public async Task SearchInvoices_maps_response_and_targets_versioned_path_with_bearer(
        string version, string expectedPath)
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"Page":0,"PageSize":100,"Total":1,"Items":[{"Id":"I1","Number":"INV-100","Status":"Invoiced","Total":{"Amount":1850.50,"Currency":"USD"},"AmountPaid":1000.00,"RemainingBalance":850.50,"IsSubmitted":true,"Customer":{"Id":"C1","Name":"Acme"},"LineItems":[{"Id":"LI1","Description":"Linehaul","Type":"Linehaul","Amount":1500.00}],"Loads":[{"Id":"L1","LoadNumber":"100"}]}]}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>(), version);

        var result = await client.SearchInvoicesAsync(new InvoiceSearchRequest { LoadNumbers = ["100"] });

        var item = Assert.Single(result.Items);
        Assert.Equal("I1", item.Id);
        Assert.Equal("INV-100", item.Number);
        Assert.Equal(1850.50m, item.Total?.Amount);
        Assert.Equal("USD", item.Total?.Currency);
        Assert.Equal(850.50m, item.RemainingBalance);
        Assert.True(item.IsSubmitted);
        Assert.Equal("Acme", item.Customer?.Name);
        Assert.Equal("Linehaul", Assert.Single(item.LineItems!).Type);
        Assert.Equal("100", Assert.Single(item.Loads!).LoadNumber);
        Assert.Equal(expectedPath, handler.Calls[0].Request.RequestUri?.AbsolutePath);
        Assert.Equal(HttpMethod.Post, handler.Calls[0].Request.Method);
        Assert.Equal("Bearer test-token", handler.Calls[0].Request.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task SearchInvoices_serializes_only_supplied_filters_in_pascal_case()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"Items":[]}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await client.SearchInvoicesAsync(new InvoiceSearchRequest { Page = 2, LoadNumbers = ["100"] });

        var body = handler.Calls[0].Body;
        Assert.Contains("\"Page\":2", body);
        Assert.Contains("\"LoadNumbers\":[\"100\"]", body);
        Assert.DoesNotContain("Status", body);             // null filters are omitted
        Assert.DoesNotContain("InvoicedDateRange", body);
        Assert.DoesNotContain("CustomerId", body);
    }

    [Fact]
    public async Task SearchInvoices_rejects_non_positive_page_size_before_calling_alvys()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SearchInvoicesAsync(new InvoiceSearchRequest { PageSize = 0 }));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task SearchInvoices_returns_empty_on_server_error_without_throwing()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var logger = new CapturingLogger<AlvysClient>();
        var client = Build(handler, logger);

        var result = await client.SearchInvoicesAsync(new InvoiceSearchRequest { LoadNumbers = ["100"] });

        Assert.Empty(result.Items);
        Assert.Contains("500", logger.AllText);
        Assert.DoesNotContain("test-token", logger.AllText);   // token never logged
    }

    [Theory]
    [InlineData("v1", "/api/p/v1/invoices")]
    [InlineData("2.0", "/api/p/v2.0/invoices")]   // normalized — no double "v"
    public async Task GetInvoice_maps_detail_and_targets_versioned_path_with_bearer(
        string version, string expectedPath)
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"Id":"I1","Number":"INV-100","Status":"Paid","PaidDate":"2026-01-10T00:00:00Z","Total":{"Amount":1850.50,"Currency":"USD"}}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>(), version);

        var invoice = await client.GetInvoiceAsync(new InvoiceLookup { Id = "I1" });

        Assert.NotNull(invoice);
        Assert.Equal("I1", invoice!.Id);
        Assert.Equal("INV-100", invoice.Number);
        Assert.Equal("Paid", invoice.Status);
        Assert.Equal(expectedPath, handler.Calls[0].Request.RequestUri?.AbsolutePath);
        Assert.Equal(HttpMethod.Get, handler.Calls[0].Request.Method);
        Assert.Equal("Bearer test-token", handler.Calls[0].Request.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task GetInvoice_sends_lookup_as_url_encoded_query()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"Id":"I1"}"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await client.GetInvoiceAsync(new InvoiceLookup { InvoiceNumber = "INV 100/A" });

        Assert.Contains(
            "/api/p/v1/invoices?invoiceNumber=INV%20100%2FA", handler.Calls[0].Request.RequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task GetInvoice_rejects_empty_lookup_before_calling_alvys()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetInvoiceAsync(new InvoiceLookup()));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task GetInvoice_returns_null_on_not_found()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        var logger = new CapturingLogger<AlvysClient>();
        var client = Build(handler, logger);

        Assert.Null(await client.GetInvoiceAsync(new InvoiceLookup { Id = "missing" }));
        Assert.DoesNotContain("failed with HTTP", logger.AllText);   // 404 is not an error
    }

    // ----- Visibility history ----------------------------------------------

    [Theory]
    [InlineData("v1", "/api/p/v1/visibility/inbound/100/history")]
    [InlineData("2.0", "/api/p/v2.0/visibility/inbound/100/history")]   // normalized — no double "v"
    public async Task ListInboundVisibilityHistory_maps_bare_array_and_targets_versioned_path_with_bearer(
        string version, string expectedPath)
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """[{"Id":"V1","ExternalId":"E1","TripNumber":"500","LoadNumber":"100","EventType":"LocationUpdate","SharedAt":"2026-01-02T03:04:05Z","Status":"Failed","Error":"Carrier not connected","Address":{"Street":"1 Main","City":"Dallas","State":"TX","ZipCode":"75201"},"Coordinates":{"Latitude":32.7,"Longitude":-96.8}}]"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>(), version);

        var events = await client.ListInboundVisibilityHistoryAsync("100");

        var item = Assert.Single(events);
        Assert.Equal("V1", item.Id);
        Assert.Equal("100", item.LoadNumber);
        Assert.Equal("Failed", item.Status);
        Assert.Equal("Carrier not connected", item.Error);
        Assert.Equal("75201", item.Address?.ZipCode);
        Assert.Equal(32.7, item.Coordinates?.Latitude);
        Assert.Equal(expectedPath, handler.Calls[0].Request.RequestUri?.AbsolutePath);
        Assert.Equal(HttpMethod.Get, handler.Calls[0].Request.Method);
        Assert.Equal("Bearer test-token", handler.Calls[0].Request.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task ListOutboundVisibilityHistory_targets_versioned_path()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""[{"Id":"V2","LoadNumber":"100","EventType":"StatusUpdate"}]"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        var events = await client.ListOutboundVisibilityHistoryAsync("100");

        Assert.Equal("V2", Assert.Single(events).Id);
        Assert.Equal("/api/p/v1/visibility/outbound/100/history", handler.Calls[0].Request.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task ListInboundVisibilityHistory_url_encodes_load_number_in_path()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]"),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await client.ListInboundVisibilityHistoryAsync("VT 100/A");

        Assert.Contains(
            "/api/p/v1/visibility/inbound/VT%20100%2FA/history", handler.Calls[0].Request.RequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task ListInboundVisibilityHistory_returns_empty_on_not_found_without_logging_error()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        var logger = new CapturingLogger<AlvysClient>();
        var client = Build(handler, logger);

        Assert.Empty(await client.ListInboundVisibilityHistoryAsync("999"));
        Assert.Empty(logger.Messages);
    }

    [Fact]
    public async Task ListOutboundVisibilityHistory_returns_empty_on_server_error_without_throwing()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var logger = new CapturingLogger<AlvysClient>();
        var client = Build(handler, logger);

        Assert.Empty(await client.ListOutboundVisibilityHistoryAsync("100"));
        Assert.Contains("500", logger.AllText);
        Assert.DoesNotContain("test-token", logger.AllText);   // token never logged
    }

    // ----- Truck / trailer events ------------------------------------------

    [Theory]
    [InlineData("v1", "/api/p/v1/trucks/events/search")]
    [InlineData("2.0", "/api/p/v2.0/trucks/events/search")]   // normalized — no double "v"
    public async Task SearchTruckEvents_maps_bare_array_and_targets_versioned_path_with_bearer(
        string version, string expectedPath)
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """[{"Id":"EV1","TruckId":"TK1","Title":"Brake repair","EventType":"Repair","Description":"In shop","StartDate":"2026-01-02T00:00:00Z","EndDate":"2026-01-03T00:00:00Z","CreatedBy":"jane","CreatedAt":"2026-01-01T00:00:00Z"}]"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>(), version);

        var events = await client.SearchTruckEventsAsync(new TruckEventSearchRequest
        {
            StartDate = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            TruckIds = ["TK1"],
        });

        var item = Assert.Single(events);
        Assert.Equal("EV1", item.Id);
        Assert.Equal("TK1", item.TruckId);
        Assert.Equal("Repair", item.EventType);
        Assert.Equal(expectedPath, handler.Calls[0].Request.RequestUri?.AbsolutePath);
        Assert.Equal(HttpMethod.Post, handler.Calls[0].Request.Method);
        Assert.Equal("Bearer test-token", handler.Calls[0].Request.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task SearchTruckEvents_serializes_required_fields_and_omits_null_end_date()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]"),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await client.SearchTruckEventsAsync(new TruckEventSearchRequest
        {
            StartDate = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            TruckIds = ["TK1", "TK2"],
        });

        var body = handler.Calls[0].Body;
        Assert.Contains("\"StartDate\":", body);
        Assert.Contains("\"TruckIds\":[\"TK1\",\"TK2\"]", body);
        Assert.DoesNotContain("EndDate", body);   // null end is omitted
    }

    [Fact]
    public async Task SearchTruckEvents_rejects_missing_start_date_before_calling_alvys()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SearchTruckEventsAsync(new TruckEventSearchRequest { TruckIds = ["TK1"] }));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task SearchTruckEvents_rejects_empty_truck_ids_before_calling_alvys()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SearchTruckEventsAsync(new TruckEventSearchRequest
            {
                StartDate = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            }));
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData("v1", "/api/p/v1/trailers/events/search")]
    [InlineData("2.0", "/api/p/v2.0/trailers/events/search")]   // normalized — no double "v"
    public async Task SearchTrailerEvents_maps_bare_array_and_targets_versioned_path_with_bearer(
        string version, string expectedPath)
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """[{"Id":"EV9","TrailerId":"TR1","Title":"Reefer service","EventType":"Maintenance","StartDate":"2026-01-02T00:00:00Z"}]"""),
        });
        var client = Build(handler, new CapturingLogger<AlvysClient>(), version);

        var events = await client.SearchTrailerEventsAsync(new TrailerEventSearchRequest
        {
            StartDate = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            TrailerIds = ["TR1"],
        });

        var item = Assert.Single(events);
        Assert.Equal("EV9", item.Id);
        Assert.Equal("TR1", item.TrailerId);
        Assert.Equal("Maintenance", item.EventType);
        Assert.Equal(expectedPath, handler.Calls[0].Request.RequestUri?.AbsolutePath);
        Assert.Equal(HttpMethod.Post, handler.Calls[0].Request.Method);
        Assert.Equal("Bearer test-token", handler.Calls[0].Request.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task SearchTrailerEvents_rejects_empty_trailer_ids_before_calling_alvys()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var client = Build(handler, new CapturingLogger<AlvysClient>());

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SearchTrailerEventsAsync(new TrailerEventSearchRequest
            {
                StartDate = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            }));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task SearchTrailerEvents_returns_empty_on_rate_limit_without_throwing()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var logger = new CapturingLogger<AlvysClient>();
        var client = Build(handler, logger);

        var result = await client.SearchTrailerEventsAsync(new TrailerEventSearchRequest
        {
            StartDate = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            TrailerIds = ["TR1"],
        });

        Assert.Empty(result);
        Assert.Contains("429", logger.AllText);
        Assert.DoesNotContain("test-token", logger.AllText);   // token never logged
    }
}
