using LtlTool.Api.Features.Ltl.Consolidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Consolidation;

/// <summary>
/// Standalone-deployment guard. The Azure App Service standalone image ships with NO app
/// settings for the consolidation section (Kudu can't persist them). The pilot corridor must
/// therefore live in the C# option defaults and survive binding an EMPTY configuration — that
/// is what keeps the Consolidate tab's corridor banner, picker, and (once a live parent exists)
/// the candidate queue alive with zero external config. An env/app-settings override still wins;
/// this test only proves the zero-config floor.
/// </summary>
public class ConsolidationOptionsBindingTests
{
    [Fact]
    public void Defaults_bind_with_empty_configuration_yielding_the_Laredo_Dallas_pilot()
    {
        // No config sources at all — the standalone image's reality.
        var configuration = new ConfigurationBuilder().Build();

        var provider = new ServiceCollection()
            .AddOptions<ConsolidationOptions>()
            .Bind(configuration.GetSection(ConsolidationOptions.SectionName))
            .Services
            .BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<ConsolidationOptions>>().Value;

        Assert.True(options.Enabled);

        var corridor = Assert.Single(options.Corridors);
        Assert.Equal("LAREDO_TO_DALLAS", corridor.Code);
        Assert.Equal("LAREDO", corridor.OriginWarehouseCode);
        Assert.Equal("DALLAS", corridor.DestinationWarehouseCode);

        Assert.Contains(options.Warehouses, w => w.Code == "LAREDO");
        Assert.Contains(options.Warehouses, w => w.Code == "DALLAS");
    }

    [Fact]
    public void Scalar_environment_override_still_wins_over_the_default()
    {
        // A scalar override (as an env var / app setting would supply) must take effect. Note:
        // the .NET config binder APPENDS to List<T> defaults rather than replacing them, so a
        // corridor override via config would duplicate the default corridor — that is why the
        // pilot corridor lives in C# defaults and is not mirrored into appsettings.json. Scalar
        // properties, by contrast, override cleanly, which is what this test pins.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ltl:Consolidation:MaxCandidatesReturned"] = "50",
                ["Ltl:Consolidation:Enabled"] = "false",
            })
            .Build();

        var provider = new ServiceCollection()
            .AddOptions<ConsolidationOptions>()
            .Bind(configuration.GetSection(ConsolidationOptions.SectionName))
            .Services
            .BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<ConsolidationOptions>>().Value;

        Assert.Equal(50, options.MaxCandidatesReturned);
        Assert.False(options.Enabled);
        // The default corridor is untouched by the scalar overrides.
        Assert.Equal("LAREDO_TO_DALLAS", Assert.Single(options.Corridors).Code);
    }
}
