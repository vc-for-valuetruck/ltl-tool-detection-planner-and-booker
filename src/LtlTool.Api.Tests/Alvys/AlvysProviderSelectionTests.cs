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
}
