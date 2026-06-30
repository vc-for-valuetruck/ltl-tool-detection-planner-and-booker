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
        public string? DocumentsLoadNumber { get; private set; }
        public string? NotesLoadNumber { get; private set; }
        public LoadLookup? LoadLookup { get; private set; }
        public TripLookup? TripLookup { get; private set; }
        public string? StopsTripId { get; private set; }
        public InvoiceSearchRequest? Invoices { get; private set; }
        public InvoiceLookup? InvoiceLookup { get; private set; }
        public string? InboundVisibilityLoadNumber { get; private set; }
        public string? OutboundVisibilityLoadNumber { get; private set; }
        public TruckEventSearchRequest? TruckEvents { get; private set; }
        public TrailerEventSearchRequest? TrailerEvents { get; private set; }
        public AlvysLoad? LoadToReturn { get; set; } =
            new() { Id = "L1", LoadNumber = "100", OrderNumber = "O-9", Status = "Delivered" };
        public AlvysTrip? TripToReturn { get; set; } =
            new() { Id = "T1", TripNumber = "500", Status = "Delivered" };
        public AlvysInvoice? InvoiceToReturn { get; set; } =
            new() { Id = "I1", Number = "INV-100", Status = "Invoiced" };

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

        public Task<AlvysLoad?> GetLoadAsync(LoadLookup lookup, CancellationToken ct = default)
        {
            LoadLookup = lookup;
            return Task.FromResult(LoadToReturn);
        }

        public Task<AlvysTrip?> GetTripAsync(TripLookup lookup, CancellationToken ct = default)
        {
            TripLookup = lookup;
            return Task.FromResult(TripToReturn);
        }

        public Task<IReadOnlyList<AlvysTripStopDetail>> ListTripStopsAsync(string tripId, CancellationToken ct = default)
        {
            StopsTripId = tripId;
            return Task.FromResult<IReadOnlyList<AlvysTripStopDetail>>(
                [new AlvysTripStopDetail { Type = "appointment", Id = "S1", StopType = "Pickup" }]);
        }

        public Task<IReadOnlyList<AlvysLoadDocument>> ListLoadDocumentsAsync(string loadNumber, CancellationToken ct = default)
        {
            DocumentsLoadNumber = loadNumber;
            return Task.FromResult<IReadOnlyList<AlvysLoadDocument>>(
                [new AlvysLoadDocument { Id = "DOC1", AttachmentType = "RateConfirmation", DownloadUrl = "https://files.alvys.test/abc" }]);
        }

        public Task<IReadOnlyList<AlvysLoadNote>> ListLoadNotesAsync(string loadNumber, CancellationToken ct = default)
        {
            NotesLoadNumber = loadNumber;
            return Task.FromResult<IReadOnlyList<AlvysLoadNote>>(
                [new AlvysLoadNote { Id = "N1", Description = "Detention approved", NoteType = "Operations" }]);
        }

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

        public Task<AlvysInvoicesResponse> SearchInvoicesAsync(InvoiceSearchRequest request, CancellationToken ct = default)
        {
            Invoices = request;
            return Task.FromResult(new AlvysInvoicesResponse
            {
                Total = 1,
                Items = [new AlvysInvoice { Id = "I1", Number = "INV-100", Status = "Invoiced" }],
            });
        }

        public Task<AlvysInvoice?> GetInvoiceAsync(InvoiceLookup lookup, CancellationToken ct = default)
        {
            InvoiceLookup = lookup;
            return Task.FromResult(InvoiceToReturn);
        }

        public Task<IReadOnlyList<AlvysVisibilityHistoryEvent>> ListInboundVisibilityHistoryAsync(
            string loadNumber, CancellationToken ct = default)
        {
            InboundVisibilityLoadNumber = loadNumber;
            return Task.FromResult<IReadOnlyList<AlvysVisibilityHistoryEvent>>(
                [new AlvysVisibilityHistoryEvent { Id = "V1", LoadNumber = loadNumber, EventType = "LocationUpdate" }]);
        }

        public Task<IReadOnlyList<AlvysVisibilityHistoryEvent>> ListOutboundVisibilityHistoryAsync(
            string loadNumber, CancellationToken ct = default)
        {
            OutboundVisibilityLoadNumber = loadNumber;
            return Task.FromResult<IReadOnlyList<AlvysVisibilityHistoryEvent>>(
                [new AlvysVisibilityHistoryEvent { Id = "V2", LoadNumber = loadNumber, EventType = "StatusUpdate" }]);
        }

        public Task<IReadOnlyList<AlvysTruckEvent>> SearchTruckEventsAsync(
            TruckEventSearchRequest request, CancellationToken ct = default)
        {
            TruckEvents = request;
            return Task.FromResult<IReadOnlyList<AlvysTruckEvent>>(
                [new AlvysTruckEvent { Id = "EV1", TruckId = "TK1", EventType = "Repair" }]);
        }

        public Task<IReadOnlyList<AlvysTrailerEvent>> SearchTrailerEventsAsync(
            TrailerEventSearchRequest request, CancellationToken ct = default)
        {
            TrailerEvents = request;
            return Task.FromResult<IReadOnlyList<AlvysTrailerEvent>>(
                [new AlvysTrailerEvent { Id = "EV9", TrailerId = "TR1", EventType = "Maintenance" }]);
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
    public async Task ListLoadDocuments_passes_load_number_through_and_returns_response()
    {
        var client = new RecordingAlvysClient();
        var controller = new AlvysSearchController(client);

        var result = await controller.ListLoadDocuments("100", default);
        var body = Assert.IsAssignableFrom<IReadOnlyList<AlvysLoadDocument>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal("100", client.DocumentsLoadNumber);
        Assert.Equal("DOC1", Assert.Single(body).Id);
    }

    [Fact]
    public async Task ListLoadNotes_passes_load_number_through_and_returns_response()
    {
        var client = new RecordingAlvysClient();
        var controller = new AlvysSearchController(client);

        var result = await controller.ListLoadNotes("100", default);
        var body = Assert.IsAssignableFrom<IReadOnlyList<AlvysLoadNote>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal("100", client.NotesLoadNumber);
        Assert.Equal("Detention approved", Assert.Single(body).Description);
    }

    [Fact]
    public async Task GetLoad_passes_lookup_through_and_returns_load_when_found()
    {
        var client = new RecordingAlvysClient();
        var controller = new AlvysSearchController(client);
        var lookup = new LoadLookup { LoadNumber = "100" };

        var result = await controller.GetLoad(lookup, default);
        var body = Assert.IsType<AlvysLoad>(Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Same(lookup, client.LoadLookup);
        Assert.Equal("100", body.LoadNumber);
    }

    [Fact]
    public async Task GetLoad_returns_400_when_no_criteria_supplied()
    {
        var client = new RecordingAlvysClient();
        var controller = new AlvysSearchController(client);

        var result = await controller.GetLoad(new LoadLookup(), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Null(client.LoadLookup);   // never reaches the client
    }

    [Fact]
    public async Task GetLoad_returns_404_when_not_found()
    {
        var client = new RecordingAlvysClient { LoadToReturn = null };
        var controller = new AlvysSearchController(client);

        var result = await controller.GetLoad(new LoadLookup { Id = "missing" }, default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetTrip_passes_lookup_through_and_returns_trip_when_found()
    {
        var client = new RecordingAlvysClient();
        var controller = new AlvysSearchController(client);
        var lookup = new TripLookup { TripNumber = "500", IncludeDeleted = true };

        var result = await controller.GetTrip(lookup, default);
        var body = Assert.IsType<AlvysTrip>(Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Same(lookup, client.TripLookup);
        Assert.Equal("500", body.TripNumber);
    }

    [Fact]
    public async Task GetTrip_returns_400_when_no_criteria_supplied()
    {
        var client = new RecordingAlvysClient();
        var controller = new AlvysSearchController(client);

        var result = await controller.GetTrip(new TripLookup { IncludeDeleted = true }, default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Null(client.TripLookup);   // never reaches the client
    }

    [Fact]
    public async Task GetTrip_returns_404_when_not_found()
    {
        var client = new RecordingAlvysClient { TripToReturn = null };
        var controller = new AlvysSearchController(client);

        var result = await controller.GetTrip(new TripLookup { Id = "missing" }, default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task ListTripStops_passes_trip_id_through_and_returns_response()
    {
        var client = new RecordingAlvysClient();
        var controller = new AlvysSearchController(client);

        var result = await controller.ListTripStops("T1", default);
        var body = Assert.IsAssignableFrom<IReadOnlyList<AlvysTripStopDetail>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal("T1", client.StopsTripId);
        Assert.Equal("S1", Assert.Single(body).Id);
    }

    [Fact]
    public async Task SearchInvoices_passes_request_through_and_returns_response()
    {
        var client = new RecordingAlvysClient();
        var controller = new AlvysSearchController(client);
        var request = new InvoiceSearchRequest { LoadNumbers = ["100"] };

        var body = Body(await controller.SearchInvoices(request, default));

        Assert.Same(request, client.Invoices);
        Assert.Equal("INV-100", Assert.Single(body.Items).Number);
    }

    [Fact]
    public async Task GetInvoice_passes_lookup_through_and_returns_invoice_when_found()
    {
        var client = new RecordingAlvysClient();
        var controller = new AlvysSearchController(client);
        var lookup = new InvoiceLookup { InvoiceNumber = "INV-100" };

        var result = await controller.GetInvoice(lookup, default);
        var body = Assert.IsType<AlvysInvoice>(Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Same(lookup, client.InvoiceLookup);
        Assert.Equal("INV-100", body.Number);
    }

    [Fact]
    public async Task GetInvoice_returns_400_when_no_criteria_supplied()
    {
        var client = new RecordingAlvysClient();
        var controller = new AlvysSearchController(client);

        var result = await controller.GetInvoice(new InvoiceLookup(), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Null(client.InvoiceLookup);   // never reaches the client
    }

    [Fact]
    public async Task GetInvoice_returns_404_when_not_found()
    {
        var client = new RecordingAlvysClient { InvoiceToReturn = null };
        var controller = new AlvysSearchController(client);

        var result = await controller.GetInvoice(new InvoiceLookup { Id = "missing" }, default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task ListInboundVisibilityHistory_passes_load_number_through_and_returns_response()
    {
        var client = new RecordingAlvysClient();
        var controller = new AlvysSearchController(client);

        var result = await controller.ListInboundVisibilityHistory("100", default);
        var body = Assert.IsAssignableFrom<IReadOnlyList<AlvysVisibilityHistoryEvent>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal("100", client.InboundVisibilityLoadNumber);
        Assert.Equal("V1", Assert.Single(body).Id);
    }

    [Fact]
    public async Task ListOutboundVisibilityHistory_passes_load_number_through_and_returns_response()
    {
        var client = new RecordingAlvysClient();
        var controller = new AlvysSearchController(client);

        var result = await controller.ListOutboundVisibilityHistory("100", default);
        var body = Assert.IsAssignableFrom<IReadOnlyList<AlvysVisibilityHistoryEvent>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal("100", client.OutboundVisibilityLoadNumber);
        Assert.Equal("V2", Assert.Single(body).Id);
    }

    [Fact]
    public async Task SearchTruckEvents_passes_request_through_and_returns_response()
    {
        var client = new RecordingAlvysClient();
        var controller = new AlvysSearchController(client);
        var request = new TruckEventSearchRequest
        {
            StartDate = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            TruckIds = ["TK1"],
        };

        var result = await controller.SearchTruckEvents(request, default);
        var body = Assert.IsAssignableFrom<IReadOnlyList<AlvysTruckEvent>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Same(request, client.TruckEvents);
        Assert.Equal("EV1", Assert.Single(body).Id);
    }

    [Fact]
    public async Task SearchTrailerEvents_passes_request_through_and_returns_response()
    {
        var client = new RecordingAlvysClient();
        var controller = new AlvysSearchController(client);
        var request = new TrailerEventSearchRequest
        {
            StartDate = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            TrailerIds = ["TR1"],
        };

        var result = await controller.SearchTrailerEvents(request, default);
        var body = Assert.IsAssignableFrom<IReadOnlyList<AlvysTrailerEvent>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Same(request, client.TrailerEvents);
        Assert.Equal("EV9", Assert.Single(body).Id);
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
            JsonSerializer.Serialize(((OkObjectResult)(await controller.ListLoadDocuments("100", default)).Result!).Value),
            JsonSerializer.Serialize(((OkObjectResult)(await controller.ListLoadNotes("100", default)).Result!).Value),
            JsonSerializer.Serialize(Assert.IsType<AlvysLoad>(((OkObjectResult)(await controller.GetLoad(new LoadLookup { Id = "L1" }, default)).Result!).Value)),
            JsonSerializer.Serialize(Assert.IsType<AlvysTrip>(((OkObjectResult)(await controller.GetTrip(new TripLookup { Id = "T1" }, default)).Result!).Value)),
            JsonSerializer.Serialize(((OkObjectResult)(await controller.ListTripStops("T1", default)).Result!).Value),
            JsonSerializer.Serialize(Body(await controller.SearchInvoices(new InvoiceSearchRequest { LoadNumbers = ["100"] }, default))),
            JsonSerializer.Serialize(Assert.IsType<AlvysInvoice>(((OkObjectResult)(await controller.GetInvoice(new InvoiceLookup { Id = "I1" }, default)).Result!).Value)),
            JsonSerializer.Serialize(((OkObjectResult)(await controller.ListInboundVisibilityHistory("100", default)).Result!).Value),
            JsonSerializer.Serialize(((OkObjectResult)(await controller.ListOutboundVisibilityHistory("100", default)).Result!).Value),
            JsonSerializer.Serialize(((OkObjectResult)(await controller.SearchTruckEvents(new TruckEventSearchRequest { StartDate = DateTimeOffset.Parse("2026-01-01T00:00:00Z"), TruckIds = ["TK1"] }, default)).Result!).Value),
            JsonSerializer.Serialize(((OkObjectResult)(await controller.SearchTrailerEvents(new TrailerEventSearchRequest { StartDate = DateTimeOffset.Parse("2026-01-01T00:00:00Z"), TrailerIds = ["TR1"] }, default)).Result!).Value),
        };

        string[] forbidden =
            ["client_secret", "ClientSecret", "access_token", "AccessToken", "Bearer", "ClientId", "TokenUrl"];
        foreach (var payload in payloads)
            foreach (var term in forbidden)
                Assert.DoesNotContain(term, payload, StringComparison.OrdinalIgnoreCase);
    }
}
