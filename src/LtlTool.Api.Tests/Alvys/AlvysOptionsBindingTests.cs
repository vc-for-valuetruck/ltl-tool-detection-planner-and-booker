using LtlTool.Api.Features.Integrations.Alvys;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LtlTool.Api.Tests.Alvys;

public sealed class AlvysOptionsBindingTests
{
    private static AlvysOptions Bind(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return config.GetSection(AlvysOptions.SectionName).Get<AlvysOptions>() ?? new AlvysOptions();
    }

    [Fact]
    public void Defaults_to_live_provider_when_unset()
    {
        var options = new AlvysOptions();
        Assert.Equal(AlvysProvider.Live, options.Provider);
        Assert.False(options.HasCredentials);
    }

    [Fact]
    public void Binds_values_from_configuration()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["Alvys:Provider"] = "Fallback",
            ["Alvys:ApiBaseUrl"] = "https://example.test/api",
            ["Alvys:ClientId"] = "cid",
            ["Alvys:ClientSecret"] = "secret",
            ["Alvys:TimeoutSeconds"] = "12",
        });

        Assert.Equal(AlvysProvider.Fallback, options.Provider);
        Assert.Equal("https://example.test/api", options.ApiBaseUrl);
        Assert.Equal(12, options.TimeoutSeconds);
        Assert.True(options.HasCredentials);
    }

    [Fact]
    public void HasCredentials_requires_both_id_and_secret()
    {
        Assert.False(Bind(new() { ["Alvys:ClientId"] = "cid" }).HasCredentials);
        Assert.False(Bind(new() { ["Alvys:ClientSecret"] = "secret" }).HasCredentials);
    }
}
