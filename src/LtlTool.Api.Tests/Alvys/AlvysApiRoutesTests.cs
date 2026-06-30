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
    public void Search_paths_are_relative_so_they_resolve_under_the_host_base()
    {
        var baseAddress = new Uri("https://integrations.alvys.com/");
        var resolved = new Uri(baseAddress, AlvysApiRoutes.TripsSearch("v1"));
        Assert.Equal("https://integrations.alvys.com/api/p/v1/trips/search", resolved.ToString());
    }
}
