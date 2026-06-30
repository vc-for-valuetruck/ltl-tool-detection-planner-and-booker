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
    public void Search_paths_are_relative_so_they_resolve_under_the_host_base()
    {
        var baseAddress = new Uri("https://integrations.alvys.com/");
        var resolved = new Uri(baseAddress, AlvysApiRoutes.TripsSearch("v1"));
        Assert.Equal("https://integrations.alvys.com/api/p/v1/trips/search", resolved.ToString());
    }
}
