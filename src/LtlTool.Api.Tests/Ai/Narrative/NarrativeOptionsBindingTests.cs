using LtlTool.Api.Features.Ai;
using LtlTool.Api.Features.Ai.Narrative;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LtlTool.Api.Tests.Ai.Narrative;

/// <summary>
/// Verifies the <c>AI</c> config section binds to <see cref="AiFeatureFlags"/> and
/// <see cref="AzureOpenAiOptions"/> as wired by <see cref="AiServiceCollectionExtensions"/>.
/// Confirms the fail-closed default (NarrativeEnabled = false) and that no API-key field exists.
/// </summary>
public sealed class NarrativeOptionsBindingTests
{
    private static (AiFeatureFlags Flags, AzureOpenAiOptions Azure) Bind(Dictionary<string, string?> config)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddAiNarrative(configuration);
        using var provider = services.BuildServiceProvider();
        return (
            provider.GetRequiredService<IOptionsMonitor<AiFeatureFlags>>().CurrentValue,
            provider.GetRequiredService<IOptions<AzureOpenAiOptions>>().Value);
    }

    [Fact]
    public void Defaults_are_fail_closed()
    {
        var (flags, azure) = Bind([]);

        Assert.False(flags.NarrativeEnabled);
        Assert.False(azure.IsConfigured);
        Assert.Equal("2024-06-01", azure.ApiVersion);
    }

    [Fact]
    public void Binds_provided_values()
    {
        var (flags, azure) = Bind(new()
        {
            ["AI:NarrativeEnabled"] = "true",
            ["AI:AzureOpenAI:Endpoint"] = "https://example.openai.azure.com/",
            ["AI:AzureOpenAI:Deployment"] = "gpt-4o",
            ["AI:AzureOpenAI:ApiVersion"] = "2024-10-01",
        });

        Assert.True(flags.NarrativeEnabled);
        Assert.True(azure.IsConfigured);
        Assert.Equal("gpt-4o", azure.Deployment);
        Assert.Equal("2024-10-01", azure.ApiVersion);
    }
}
