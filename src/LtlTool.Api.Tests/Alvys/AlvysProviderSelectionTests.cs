using LtlTool.Api.Features.Integrations.Alvys;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LtlTool.Api.Tests.Alvys;

public sealed class AlvysProviderSelectionTests
{
    private static IAlvysClient ResolveClient(Dictionary<string, string?> config)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAlvysIntegration(configuration);

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IAlvysClient>();
    }

    [Fact]
    public void Defaults_to_live_client_when_provider_unset()
        => Assert.IsType<AlvysClient>(ResolveClient([]));

    [Fact]
    public void Selects_live_client_for_live_provider()
        => Assert.IsType<AlvysClient>(ResolveClient(new() { ["Alvys:Provider"] = "Live" }));

    [Fact]
    public void Selects_fallback_client_only_when_explicitly_requested()
        => Assert.IsType<FallbackAlvysClient>(ResolveClient(new() { ["Alvys:Provider"] = "Fallback" }));

    [Fact]
    public void Live_remains_default_even_when_credentials_absent()
        => Assert.IsType<AlvysClient>(ResolveClient(new() { ["Alvys:ClientId"] = "", ["Alvys:ClientSecret"] = "" }));

    [Fact]
    public async Task Fallback_client_returns_empty_paged_shapes_for_loads_and_trips()
    {
        var client = ResolveClient(new() { ["Alvys:Provider"] = "Fallback" });

        var loads = await client.SearchLoadsAsync(new LoadSearchRequest { Status = ["Open"] });
        var trips = await client.SearchTripsAsync(new TripSearchRequest { Status = ["In Transit"] });

        Assert.Empty(loads.Items);
        Assert.Empty(trips.Items);
        Assert.Null(await client.GetLoadByNumberAsync("123"));
    }

    [Fact]
    public async Task Fallback_client_returns_empty_paged_shapes_for_trailers_and_trucks()
    {
        var client = ResolveClient(new() { ["Alvys:Provider"] = "Fallback" });

        var trailers = await client.SearchTrailersAsync(new TrailerSearchRequest { Status = ["Active"] });
        var trucks = await client.SearchTrucksAsync(new TruckSearchRequest { IsActive = true });

        Assert.Empty(trailers.Items);
        Assert.Empty(trucks.Items);
    }

    [Fact]
    public async Task Fallback_client_returns_empty_shapes_for_context_resources()
    {
        var client = ResolveClient(new() { ["Alvys:Provider"] = "Fallback" });

        var prefs = await client.SearchDispatchPreferencesAsync(new DispatchPreferenceSearchRequest { DispatcherIds = ["D1"] });
        var locations = await client.SearchLocationsAsync(new LocationSearchRequest { Status = ["Active"] });
        var drivers = await client.SearchDriversAsync(new DriverSearchRequest { IsActive = true });
        var customers = await client.SearchCustomersAsync(new CustomerSearchRequest { Statuses = ["Active"] });
        var users = await client.SearchUsersAsync(new UserSearchRequest { Keyword = "x" });

        Assert.Empty(prefs);
        Assert.Empty(locations.Items);
        Assert.Empty(drivers.Items);
        Assert.Empty(customers.Items);
        Assert.Empty(users.Items);
    }

    [Fact]
    public async Task Fallback_client_returns_empty_paged_shape_and_null_for_tenders()
    {
        var client = ResolveClient(new() { ["Alvys:Provider"] = "Fallback" });

        var tenders = await client.SearchTendersAsync(
            new TenderSearchRequest { Filter = new TenderSearchFilter { Status = ["Offered"] } });

        Assert.Empty(tenders.Items);
        Assert.Null(await client.GetTenderByIdAsync("TEN1"));
    }

    [Fact]
    public async Task Fallback_client_returns_empty_arrays_for_load_documents_and_notes()
    {
        var client = ResolveClient(new() { ["Alvys:Provider"] = "Fallback" });

        var documents = await client.ListLoadDocumentsAsync("100");
        var notes = await client.ListLoadNotesAsync("100");

        Assert.Empty(documents);
        Assert.Empty(notes);
    }

    [Fact]
    public async Task Fallback_client_returns_null_and_empty_for_load_trip_detail_and_stops()
    {
        var client = ResolveClient(new() { ["Alvys:Provider"] = "Fallback" });

        Assert.Null(await client.GetLoadAsync(new LoadLookup { Id = "L1" }));
        Assert.Null(await client.GetTripAsync(new TripLookup { Id = "T1" }));
        Assert.Empty(await client.ListTripStopsAsync("T1"));
    }

    [Fact]
    public async Task Fallback_client_returns_empty_paged_shape_and_null_for_invoices()
    {
        var client = ResolveClient(new() { ["Alvys:Provider"] = "Fallback" });

        var invoices = await client.SearchInvoicesAsync(new InvoiceSearchRequest { LoadNumbers = ["100"] });

        Assert.Empty(invoices.Items);
        Assert.Null(await client.GetInvoiceAsync(new InvoiceLookup { Id = "I1" }));
    }

    [Fact]
    public async Task Fallback_client_returns_empty_arrays_for_visibility_history()
    {
        var client = ResolveClient(new() { ["Alvys:Provider"] = "Fallback" });

        Assert.Empty(await client.ListInboundVisibilityHistoryAsync("100"));
        Assert.Empty(await client.ListOutboundVisibilityHistoryAsync("100"));
    }

    [Fact]
    public async Task Fallback_client_returns_empty_arrays_for_truck_and_trailer_events()
    {
        var client = ResolveClient(new() { ["Alvys:Provider"] = "Fallback" });

        var truckEvents = await client.SearchTruckEventsAsync(new TruckEventSearchRequest
        {
            StartDate = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            TruckIds = ["TK1"],
        });
        var trailerEvents = await client.SearchTrailerEventsAsync(new TrailerEventSearchRequest
        {
            StartDate = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            TrailerIds = ["TR1"],
        });

        Assert.Empty(truckEvents);
        Assert.Empty(trailerEvents);
    }
}
