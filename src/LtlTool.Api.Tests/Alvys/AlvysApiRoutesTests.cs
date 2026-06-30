using LtlTool.Api.Features.Integrations.Alvys;
using Xunit;

namespace LtlTool.Api.Tests.Alvys;

public sealed class AlvysApiRoutesTests
{
    [Theory]
    [InlineData("v1", "v1")]
    [InlineData("V1", "v1")]
    [InlineData("1", "v1")]
    [InlineData("v2.0", "v2.0")]
    [InlineData("2.0", "v2.0")]
    [InlineData("  v2.0  ", "v2.0")]
    [InlineData(null, "v1")]
    [InlineData("", "v1")]
    [InlineData("   ", "v1")]
    public void NormalizeVersion_avoids_double_v_and_falls_back(string? input, string expected)
        => Assert.Equal(expected, AlvysApiRoutes.NormalizeVersion(input));

    [Fact]
    public void LoadsSearch_builds_relative_versioned_path()
        => Assert.Equal("api/p/v2.0/loads/search", AlvysApiRoutes.LoadsSearch("v2.0"));

    [Fact]
    public void TripsSearch_builds_relative_versioned_path()
        => Assert.Equal("api/p/v1/trips/search", AlvysApiRoutes.TripsSearch("v1"));

    [Fact]
    public void TrailersSearch_builds_relative_versioned_path()
        => Assert.Equal("api/p/v1/trailers/search", AlvysApiRoutes.TrailersSearch("1"));

    [Fact]
    public void TrucksSearch_builds_relative_versioned_path()
        => Assert.Equal("api/p/v2.0/trucks/search", AlvysApiRoutes.TrucksSearch("2.0"));

    [Fact]
    public void DispatchPreferencesSearch_builds_relative_versioned_path()
        => Assert.Equal(
            "api/p/v1/dispatchpreferences/search", AlvysApiRoutes.DispatchPreferencesSearch("v1"));

    [Fact]
    public void LocationsSearch_builds_relative_versioned_path()
        => Assert.Equal("api/p/v2.0/locations/search", AlvysApiRoutes.LocationsSearch("2.0"));

    [Fact]
    public void DriversSearch_builds_relative_versioned_path()
        => Assert.Equal("api/p/v1/drivers/search", AlvysApiRoutes.DriversSearch("1"));

    [Fact]
    public void CustomersSearch_builds_relative_versioned_path()
        => Assert.Equal("api/p/v2.0/customers/search", AlvysApiRoutes.CustomersSearch("v2.0"));

    [Fact]
    public void UsersSearch_builds_relative_versioned_path()
        => Assert.Equal("api/p/v1/users/search", AlvysApiRoutes.UsersSearch(null));

    [Fact]
    public void TendersSearch_builds_relative_versioned_path()
        => Assert.Equal("api/p/v2.0/tenders/search", AlvysApiRoutes.TendersSearch("2.0"));

    [Fact]
    public void TenderById_builds_relative_versioned_path()
        => Assert.Equal("api/p/v1/tenders/T-100", AlvysApiRoutes.TenderById("v1", "T-100"));

    [Theory]
    [InlineData("a/b", "api/p/v1/tenders/a%2Fb")]
    [InlineData("a b", "api/p/v1/tenders/a%20b")]
    [InlineData("a#b?c", "api/p/v1/tenders/a%23b%3Fc")]
    public void TenderById_url_encodes_the_tender_id(string tenderId, string expected)
        => Assert.Equal(expected, AlvysApiRoutes.TenderById("v1", tenderId));

    [Fact]
    public void LoadDocuments_builds_relative_versioned_path()
        => Assert.Equal("api/p/v2.0/loads/100/documents", AlvysApiRoutes.LoadDocuments("2.0", "100"));

    [Fact]
    public void LoadNotes_builds_relative_versioned_path()
        => Assert.Equal("api/p/v1/loads/100/notes", AlvysApiRoutes.LoadNotes("v1", "100"));

    [Theory]
    [InlineData("VT 100/A", "api/p/v1/loads/VT%20100%2FA/documents")]
    [InlineData("a b", "api/p/v1/loads/a%20b/documents")]
    [InlineData("x#1", "api/p/v1/loads/x%231/documents")]
    public void LoadDocuments_url_encodes_the_load_number_segment(string loadNumber, string expected)
        => Assert.Equal(expected, AlvysApiRoutes.LoadDocuments("v1", loadNumber));

    [Fact]
    public void LoadNotes_url_encodes_the_load_number_segment()
        => Assert.Equal("api/p/v1/loads/VT%20100%2FA/notes", AlvysApiRoutes.LoadNotes("v1", "VT 100/A"));

    [Fact]
    public void LoadDetail_builds_relative_versioned_path_with_id_query()
        => Assert.Equal(
            "api/p/v2.0/loads?id=L-1",
            AlvysApiRoutes.LoadDetail("2.0", new LoadLookup { Id = "L-1" }));

    [Fact]
    public void LoadDetail_builds_query_for_load_number()
        => Assert.Equal(
            "api/p/v1/loads?loadNumber=VT-100",
            AlvysApiRoutes.LoadDetail("v1", new LoadLookup { LoadNumber = "VT-100" }));

    [Fact]
    public void LoadDetail_builds_query_for_order_number()
        => Assert.Equal(
            "api/p/v1/loads?orderNumber=O-9",
            AlvysApiRoutes.LoadDetail("1", new LoadLookup { OrderNumber = "O-9" }));

    [Fact]
    public void LoadDetail_includes_all_supplied_criteria()
        => Assert.Equal(
            "api/p/v1/loads?id=L-1&loadNumber=VT-100&orderNumber=O-9",
            AlvysApiRoutes.LoadDetail(
                "v1", new LoadLookup { Id = "L-1", LoadNumber = "VT-100", OrderNumber = "O-9" }));

    [Theory]
    [InlineData("VT 100/A", "api/p/v1/loads?loadNumber=VT%20100%2FA")]
    [InlineData("a#1", "api/p/v1/loads?loadNumber=a%231")]
    [InlineData("a&b=c", "api/p/v1/loads?loadNumber=a%26b%3Dc")]
    public void LoadDetail_url_encodes_query_values(string loadNumber, string expected)
        => Assert.Equal(expected, AlvysApiRoutes.LoadDetail("v1", new LoadLookup { LoadNumber = loadNumber }));

    [Fact]
    public void LoadDetail_throws_when_no_criteria_supplied()
        => Assert.Throws<ArgumentException>(() => AlvysApiRoutes.LoadDetail("v1", new LoadLookup()));

    [Fact]
    public void TripDetail_builds_relative_versioned_path_with_id_query()
        => Assert.Equal(
            "api/p/v2.0/trips?id=T-1",
            AlvysApiRoutes.TripDetail("2.0", new TripLookup { Id = "T-1" }));

    [Fact]
    public void TripDetail_builds_query_for_trip_number()
        => Assert.Equal(
            "api/p/v1/trips?tripNumber=TR-7",
            AlvysApiRoutes.TripDetail("v1", new TripLookup { TripNumber = "TR-7" }));

    [Fact]
    public void TripDetail_appends_include_deleted_true_as_lowercase()
        => Assert.Equal(
            "api/p/v1/trips?tripNumber=TR-7&includeDeleted=true",
            AlvysApiRoutes.TripDetail("v1", new TripLookup { TripNumber = "TR-7", IncludeDeleted = true }));

    [Fact]
    public void TripDetail_appends_include_deleted_false_as_lowercase()
        => Assert.Equal(
            "api/p/v1/trips?tripNumber=TR-7&includeDeleted=false",
            AlvysApiRoutes.TripDetail("v1", new TripLookup { TripNumber = "TR-7", IncludeDeleted = false }));

    [Fact]
    public void TripDetail_omits_include_deleted_when_null()
        => Assert.Equal(
            "api/p/v1/trips?tripNumber=TR-7",
            AlvysApiRoutes.TripDetail("v1", new TripLookup { TripNumber = "TR-7", IncludeDeleted = null }));

    [Theory]
    [InlineData("a/b", "api/p/v1/trips?tripNumber=a%2Fb")]
    [InlineData("a b", "api/p/v1/trips?tripNumber=a%20b")]
    public void TripDetail_url_encodes_query_values(string tripNumber, string expected)
        => Assert.Equal(expected, AlvysApiRoutes.TripDetail("v1", new TripLookup { TripNumber = tripNumber }));

    [Fact]
    public void TripDetail_throws_when_no_criteria_supplied()
        => Assert.Throws<ArgumentException>(() => AlvysApiRoutes.TripDetail("v1", new TripLookup()));

    [Fact]
    public void TripDetail_throws_when_only_include_deleted_supplied()
        => Assert.Throws<ArgumentException>(
            () => AlvysApiRoutes.TripDetail("v1", new TripLookup { IncludeDeleted = true }));

    [Fact]
    public void TripStops_builds_relative_versioned_path()
        => Assert.Equal("api/p/v2.0/trips/T-1/stops", AlvysApiRoutes.TripStops("2.0", "T-1"));

    [Theory]
    [InlineData("a/b", "api/p/v1/trips/a%2Fb/stops")]
    [InlineData("a b", "api/p/v1/trips/a%20b/stops")]
    [InlineData("x#1", "api/p/v1/trips/x%231/stops")]
    public void TripStops_url_encodes_the_trip_id_segment(string tripId, string expected)
        => Assert.Equal(expected, AlvysApiRoutes.TripStops("v1", tripId));

    [Fact]
    public void Search_paths_are_relative_so_they_resolve_under_the_host_base()
    {
        var baseAddress = new Uri("https://integrations.alvys.com/");
        var resolved = new Uri(baseAddress, AlvysApiRoutes.TripsSearch("v1"));
        Assert.Equal("https://integrations.alvys.com/api/p/v1/trips/search", resolved.ToString());
    }
}
