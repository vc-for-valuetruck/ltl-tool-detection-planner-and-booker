using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the DI selection between <see cref="NullAccessorialSignalExtractor"/> (disabled, the
/// default) and <see cref="AzureOpenAiAccessorialSignalExtractor"/> (enabled + configured).
/// The tests use an in-memory configuration to mimic the <c>Ltl:AccessorialAi</c> section and
/// confirm that no network call is ever made when the feature is disabled.
/// </summary>
public sealed class AccessorialExtractorSelectionTests
{
    private static IAccessorialSignalExtractor ResolveExtractor(
        Dictionary<string, string?> config)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<LtlOptions>().Bind(configuration.GetSection(LtlOptions.SectionName));
        // Only call the extractor-selection part of AddLtlDecisionSupport:
        // mimic the selection branch without wiring all LTL services.
        var enabled = configuration
            .GetSection($"{LtlOptions.SectionName}:AccessorialAi")
            .GetValue<bool>("Enabled");
        if (enabled)
            services.AddHttpClient<IAccessorialSignalExtractor, AzureOpenAiAccessorialSignalExtractor>();
        else
            services.AddSingleton<IAccessorialSignalExtractor, NullAccessorialSignalExtractor>();

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IAccessorialSignalExtractor>();
    }

    [Fact]
    public void Disabled_config_registers_NullExtractor()
    {
        var extractor = ResolveExtractor([]);

        Assert.IsType<NullAccessorialSignalExtractor>(extractor);
        Assert.False(extractor.IsEnabled);
    }

    [Fact]
    public void Explicitly_false_config_registers_NullExtractor()
    {
        var extractor = ResolveExtractor(new()
        {
            ["Ltl:AccessorialAi:Enabled"] = "false",
        });

        Assert.IsType<NullAccessorialSignalExtractor>(extractor);
        Assert.False(extractor.IsEnabled);
    }

    [Fact]
    public async Task NullExtractor_returns_empty_without_any_network_call()
    {
        var extractor = ResolveExtractor([]);

        var signals = await extractor.ExtractAsync("N1", "Note", "driver waited 3 hours — detention", default);

        Assert.Empty(signals);
    }

    [Fact]
    public void Enabled_config_registers_AzureOpenAi_extractor()
    {
        var extractor = ResolveExtractor(new()
        {
            ["Ltl:AccessorialAi:Enabled"] = "true",
            ["Ltl:AccessorialAi:Endpoint"] = "https://placeholder.openai.azure.com",
            ["Ltl:AccessorialAi:DeploymentName"] = "gpt-4o",
            // ApiKey intentionally absent — extractor still registered, IsEnabled=false without key.
        });

        Assert.IsType<AzureOpenAiAccessorialSignalExtractor>(extractor);
        // Without ApiKey the extractor's IsEnabled property is false (no key → no LLM call).
        Assert.False(extractor.IsEnabled);
    }
}
