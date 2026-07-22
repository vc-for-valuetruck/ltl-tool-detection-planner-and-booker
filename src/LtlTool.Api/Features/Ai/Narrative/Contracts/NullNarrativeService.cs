namespace LtlTool.Api.Features.Ai.Narrative.Contracts;

// TODO(#149): remove once NarrativeService lands. Honest "not wired yet" fallback so the app
// boots and the endpoint behaves predictably before the real NarrativeService (#149) is
// registered. Registered with TryAdd so #149's real registration wins at merge time.

/// <summary>
/// No-op <see cref="INarrativeService"/>. Always reports "no narrative available" as a definitive,
/// non-transient result — <c>(null, Cached: false)</c> — which the endpoint maps to
/// <c>404 plan-not-found</c> when the feature flag is on. It never calls an AI provider, so a
/// fresh clone / CI / the demo never reach OpenAI. This is a placeholder, not the real generator.
/// </summary>
public sealed class NullNarrativeService : INarrativeService
{
    public Task<(NarrativeResponse? Response, bool Cached)> GenerateAsync(string planId, CancellationToken ct)
        => Task.FromResult<(NarrativeResponse?, bool)>((null, false));
}
