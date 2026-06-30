using System.Text.Json;
using LtlTool.Api.Features.Alvys;
using LtlTool.Api.Features.Integrations.Alvys;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LtlTool.Api.Tests.Alvys;

/// <summary>
/// Unit tests for the read-only <see cref="AlvysSearchController"/>. They verify each
/// action passes the request straight through to <see cref="IAlvysClient"/>, returns the
/// paged Alvys read model unchanged, and that the serialized response carries no
/// credential/secret fields.
/// </summary>
public sealed class AlvysSearchControllerTests
{
    /// <summary>Records the request it received and returns a canned response.</summary>
    private sealed class RecordingAlvysClient : IAlvysClient
    {
        public LoadSearchRequest? Loads { get; private set; }
        public TripSearchRequest? Trips { get; private set; }
        public TrailerSearchRequest? Trailers { get; private set; }
        public TruckSearchRequest? Trucks { get; private set; }
        public DispatchPreferenceSearchRequest? DispatchPreferences { get; private set; }
        public LocationSearchRequest? Locations { get; private set; }
        public DriverSearchRequest? Drivers { get; private set; }
        public CustomerSearchRequest? Customers { get; private set; }
        public UserSearchRequest? Users { get; private set; }
        public TenderSearchRequest? Tenders { get; private set; }
        public string? RequestedTenderId { get; private set; }
        public AlvysTender? TenderToReturn { get; set; } =
            new() { Id = "TEN1", Status = "Offered", LoadNumber = "100" };

        public Task<AlvysLoadsResponse> SearchLoadsAsync(
            int page = 1, int pageSize = 100, string? status = null, CancellationToken ct = default)
            => throw new NotSupportedException("Controller uses the request overload.");

        public Task<AlvysLoadsResponse> SearchLoadsAsync(LoadSearchRequest request, CancellationToken ct = default)
        {
            Loads = request;
            return Task.FromResult(new AlvysLoadsResponse
            {
                Total = 1,
                Items = [new AlvysLoad { Id = "L1", LoadNumber = "100", Status = "Open" }],
            });
        }

        public Task<AlvysLoad?> GetLoadByNumberAsync(string loadNumber, CancellationToken ct = default)
            => Task.FromResult<AlvysLoad?>(null);

        public Task<AlvysTripsResponse> SearchTripsAsync(TripSearchRequest request, CancellationToken ct = default)
        {
            Trips = request;
            return Task.FromResult(new AlvysTripsResponse
            {
                Total = 1,
                Items = [new AlvysTrip { Id = "T1", TripNumber = "500", Status = "In Transit" }],
            });
        }

        public Task<AlvysTrailersResponse> SearchTrailersAsync(TrailerSearchRequest request, CancellationToken ct = default)
        {
            Trailers = request;
            return Task.FromResult(new AlvysTrailersResponse
            {
                Total = 1,
                Items = [new AlvysTrailerEquipment { Id = "TR1", TrailerNum = "900" }],
            });
        }

        public Task<AlvysTrucksResponse> SearchTrucksAsync(TruckSearchRequest request, CancellationToken ct = default)
        {
            Trucks = request;
            return Task.FromResult(new AlvysTrucksResponse
            {
                Total = 1,
                Items = [new AlvysTruck { Id = "TK1", TruckNum = "42" }],
            });
        }

        public Task<IReadOnlyList<AlvysDispatchPreference>> SearchDispatchPreferencesAsync(
            DispatchPreferenceSearchRequest request, CancellationToken ct = default)
        {
            DispatchPreferences = request;
            return Task.FromResult<IReadOnlyList<AlvysDispatchPreference>>(
                [new AlvysDispatchPreference { DispatcherId = "D1", TruckId = "TK1" }]);
        }

        public Task<AlvysLocationsResponse> SearchLocationsAsync(LocationSearchRequest request, CancellationToken ct = default)
        {
            Locations = request;
            return Task.FromResult(new AlvysLocationsResponse
            {
                Total = 1,
                Items = [new AlvysLocation { Id = "LOC1", Name = "Dallas Hub" }],
            });
        }

        public Task<AlvysDriversResponse> SearchDriversAsync(DriverSearchRequest request, CancellationToken ct = default)
        {
            Drivers = request;
            return Task.FromResult(new AlvysDriversResponse
            {
                Total = 1,
                Items = [new AlvysDriver { Id = "DR1", Name = "Sam Driver" }],
            });
        }

        public Task<AlvysCustomersResponse> SearchCustomersAsync(CustomerSearchRequest request, CancellationToken ct = default)
        {
            Customers = request;
            return Task.FromResult(new AlvysCustomersResponse
            {
                Total = 1,
                Items = [new AlvysCustomer { Id = "C1", Name = "Acme" }],
            });
        }

        public Task<AlvysUsersResponse> SearchUsersAsync(UserSearchRequest request, CancellationToken ct = default)
        {
            Users = request;
            return Task.FromResult(new AlvysUsersResponse
            {
                Total = 1,
                Items = [new AlvysUser { Id = "U1", UserName = "jdoe" }],
            });
        }

        public Task<AlvysTendersResponse> SearchTendersAsync(TenderSearchRequest request, CancellationToken ct = default)
        {
            Tenders = request;
            return Task.FromResult(new AlvysTendersResponse
            {
                Total = 1,
                Items = [new AlvysTender { Id = "TEN1", Status = "Offered", LoadNumber = "100" }],
            });
        }

        public Task<AlvysTender?> GetTenderByIdAsync(string tenderId, CancellationToken ct = default)
        {
            RequestedTenderId = tenderId;
            return Task.FromResult(TenderToReturn);
        }
    }

    private static T Body<T>(ActionResult<T> result)
        => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result.Result).Value);

    [Fact]
    public async Task SearchLoads_passes_request_through_and_returns_response()
    {
        var client = new RecordingAlvysClient();
        var controller = new AlvysSearchController(client);
        var request = new LoadSearchRequest { Page = 3, Status = ["Open"] };

        var body = Body(await controller.SearchLoads(request, default));

        Assert.Same(request, client.Loads);
        Assert.Equal("100", Assert.Single(body.Items).LoadNumber);
    }

    [Fact]
    public async Task SearchTrips_passes_request_through_and_returns_response()
    {
        var client = new RecordingAlvysClient();
        var controller = new AlvysSearchController(client);
        var request = new TripSearchRequest { TripNumbers = ["500"] };

        var body = Body(await controller.SearchTrips(request, default));

        Assert.Same(request, client.Trips);
        Assert.Equal("500", Assert.Single(body.Items).TripNumber);
    }

    [Fact]
    public async Task SearchTrailers_passes_request_through_and_returns_response()
    {
        var client = new RecordingAlvysClient();
        var controller = new AlvysSearchController(client);
        var request = new TrailerSearchRequest { TrailerNumber = "900" };

        var body = Body(await controller.SearchTrailers(request, default));

        Assert.Same(request, client.Trailers);
        Assert.Equal("900", Assert.Single(body.Items).TrailerNum);
    }

    [Fact]
    public async Task SearchTrucks_passes_request_through_and_returns_response()
    {
        var client = new RecordingAlvysClient();
        var controller = new AlvysSearchController(client);
        var request = new TruckSearchRequest { IsActive = true };

        var body = Body(await controller.SearchTrucks(request, default));

        Assert.Same(request, client.Trucks);
        Assert.Equal("42", Assert.Single(body.Items).TruckNum);
    }

    [Fact]
    public async Task SearchDispatchPreferences_passes_request_through_and_returns_response()
    {
        var client = new RecordingAlvysClient();
        var controller = new AlvysSearchController(client);
        var request = new DispatchPreferenceSearchRequest { DispatcherIds = ["D1"] };

        var result = await controller.SearchDispatchPreferences(request, default);
        var body = Assert.IsAssignableFrom<IReadOnlyList<AlvysDispatchPreference>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Same(request, client.DispatchPreferences);
        Assert.Equal("TK1", Assert.Single(body).TruckId);
    }

    [Fact]
    public async Task SearchLocations_passes_request_through_and_returns_response()
    {
        var client = new RecordingAlvysClient();
        var controller = new AlvysSearchController(client);
        var request = new LocationSearchRequest { Status = ["Active"] };

        var body = Body(await controller.SearchLocations(request, default));

        Assert.Same(request, client.Locations);
        Assert.Equal("Dallas Hub", Assert.Single(body.Items).Name);
    }

    [Fact]
    public async Task SearchDrivers_passes_request_through_and_returns_response()
    {
        var client = new RecordingAlvysClient();
        var controller = new AlvysSearchController(client);
        var request = new DriverSearchRequest { IsActive = true };

        var body = Body(await controller.SearchDrivers(request, default));

        Assert.Same(request, client.Drivers);
        Assert.Equal("Sam Driver", Assert.Single(body.Items).Name);
    }

    [Fact]
    public async Task SearchCustomers_passes_request_through_and_returns_response()
    {
        var client = new RecordingAlvysClient();
        var controller = new AlvysSearchController(client);
        var request = new CustomerSearchRequest { Statuses = ["Active"] };

        var body = Body(await controller.SearchCustomers(request, default));

        Assert.Same(request, client.Customers);
        Assert.Equal("Acme", Assert.Single(body.Items).Name);
    }

    [Fact]
    public async Task SearchUsers_passes_request_through_and_returns_response()
    {
        var client = new RecordingAlvysClient();
        var controller = new AlvysSearchController(client);
        var request = new UserSearchRequest { Keyword = "jane" };

        var body = Body(await controller.SearchUsers(request, default));

        Assert.Same(request, client.Users);
        Assert.Equal("jdoe", Assert.Single(body.Items).UserName);
    }

    [Fact]
    public async Task SearchTenders_passes_request_through_and_returns_response()
    {
        var client = new RecordingAlvysClient();
        var controller = new AlvysSearchController(client);
        var request = new TenderSearchRequest { Filter = new TenderSearchFilter { Status = ["Offered"] } };

        var body = Body(await controller.SearchTenders(request, default));

        Assert.Same(request, client.Tenders);
        Assert.Equal("TEN1", Assert.Single(body.Items).Id);
    }

    [Fact]
    public async Task GetTender_returns_tender_when_found()
    {
        var client = new RecordingAlvysClient();
        var controller = new AlvysSearchController(client);

        var result = await controller.GetTender("TEN1", default);
        var body = Assert.IsType<AlvysTender>(Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal("TEN1", client.RequestedTenderId);
        Assert.Equal("TEN1", body.Id);
    }

    [Fact]
    public async Task GetTender_returns_404_when_not_found()
    {
        var client = new RecordingAlvysClient { TenderToReturn = null };
        var controller = new AlvysSearchController(client);

        var result = await controller.GetTender("missing", default);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Equal("missing", client.RequestedTenderId);
    }

    [Fact]
    public async Task Responses_carry_no_credential_or_secret_fields()
    {
        var client = new RecordingAlvysClient();
        var controller = new AlvysSearchController(client);

        var payloads = new[]
        {
            JsonSerializer.Serialize(Body(await controller.SearchLoads(new LoadSearchRequest { Status = ["Open"] }, default))),
            JsonSerializer.Serialize(Body(await controller.SearchTrips(new TripSearchRequest { Status = ["In Transit"] }, default))),
            JsonSerializer.Serialize(Body(await controller.SearchTrailers(new TrailerSearchRequest { Status = ["Active"] }, default))),
            JsonSerializer.Serialize(Body(await controller.SearchTrucks(new TruckSearchRequest { IsActive = true }, default))),
            JsonSerializer.Serialize(((OkObjectResult)(await controller.SearchDispatchPreferences(new DispatchPreferenceSearchRequest { DispatcherIds = ["D1"] }, default)).Result!).Value),
            JsonSerializer.Serialize(Body(await controller.SearchLocations(new LocationSearchRequest { Status = ["Active"] }, default))),
            JsonSerializer.Serialize(Body(await controller.SearchDrivers(new DriverSearchRequest { IsActive = true }, default))),
            JsonSerializer.Serialize(Body(await controller.SearchCustomers(new CustomerSearchRequest { Statuses = ["Active"] }, default))),
            JsonSerializer.Serialize(Body(await controller.SearchUsers(new UserSearchRequest { Keyword = "jane" }, default))),
            JsonSerializer.Serialize(Body(await controller.SearchTenders(new TenderSearchRequest { Filter = new TenderSearchFilter { Status = ["Offered"] } }, default))),
            JsonSerializer.Serialize(Assert.IsType<AlvysTender>(((OkObjectResult)(await controller.GetTender("TEN1", default)).Result!).Value)),
        };

        string[] forbidden =
            ["client_secret", "ClientSecret", "access_token", "AccessToken", "Bearer", "ClientId", "TokenUrl"];
        foreach (var payload in payloads)
            foreach (var term in forbidden)
                Assert.DoesNotContain(term, payload, StringComparison.OrdinalIgnoreCase);
    }
}
