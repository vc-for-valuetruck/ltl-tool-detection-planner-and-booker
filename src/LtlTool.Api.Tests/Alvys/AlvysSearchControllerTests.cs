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
        };

        string[] forbidden =
            ["client_secret", "ClientSecret", "access_token", "AccessToken", "Bearer", "ClientId", "TokenUrl"];
        foreach (var payload in payloads)
            foreach (var term in forbidden)
                Assert.DoesNotContain(term, payload, StringComparison.OrdinalIgnoreCase);
    }
}
