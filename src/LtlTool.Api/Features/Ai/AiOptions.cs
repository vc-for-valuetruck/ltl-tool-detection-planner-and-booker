namespace LtlTool.Api.Features.Ai;

/// <summary>
/// Bound from the <c>AI</c> configuration section. Only the fields the narrative HTTP
/// endpoint needs to make routing decisions live here; the AzureOpenAI transport settings
/// (<c>AI:AzureOpenAI:*</c>) are consumed by the NarrativeService (#149), not by the endpoint.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "AI";

    /// <summary>
    /// Kill switch for the AI narrative feature. Defaults to <c>false</c> so a fresh clone / CI /
    /// the demo never call an AI provider unless it is explicitly turned on server-side. When
    /// <c>false</c> the narrative endpoint returns <c>404 { "reason": "disabled" }</c>.
    /// </summary>
    public bool NarrativeEnabled { get; set; }
}
