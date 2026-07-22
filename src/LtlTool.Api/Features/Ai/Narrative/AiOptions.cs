namespace LtlTool.Api.Features.Ai.Narrative;

/// <summary>
/// Runtime feature flags for the AI layer, bound from the top-level <c>AI</c> section and read
/// through <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}"/> so the kill switch
/// can be flipped without a restart. Defaults to disabled: a fresh clone / CI / the demo never call
/// Azure OpenAI unless someone explicitly turns <see cref="NarrativeEnabled"/> on and configures a
/// server-side endpoint.
/// </summary>
public sealed class AiFeatureFlags
{
    public const string SectionName = "AI";

    /// <summary>
    /// Master kill switch for the consolidation-plan narrative. When false the
    /// <see cref="NarrativeService"/> returns <c>(null, false)</c> before any plan fetch or model
    /// call — fail-closed by default.
    /// </summary>
    public bool NarrativeEnabled { get; set; }
}

/// <summary>
/// Azure OpenAI connection settings, bound from <c>AI:AzureOpenAI</c>. Endpoint/Deployment are
/// server-side only and are never returned to the SPA. Authentication uses
/// <c>DefaultAzureCredential</c> (managed identity in Azure, developer credentials locally) — there
/// is deliberately no API-key field here, so no secret ever lands in config, source, or a screenshot.
/// </summary>
public sealed class AzureOpenAiOptions
{
    public const string SectionName = "AI:AzureOpenAI";

    /// <summary>Azure OpenAI resource endpoint, e.g. <c>https://my-resource.openai.azure.com/</c>. Empty by default.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>Chat-completions deployment name. Empty by default.</summary>
    public string Deployment { get; set; } = "";

    /// <summary>
    /// Service API version. Retained for config parity across environments; the Azure OpenAI SDK
    /// negotiates a compatible service version on its own, so this value is informational today.
    /// </summary>
    public string ApiVersion { get; set; } = "2024-06-01";

    /// <summary>True when both endpoint and deployment are present. Used to fail closed early.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(Deployment);
}
