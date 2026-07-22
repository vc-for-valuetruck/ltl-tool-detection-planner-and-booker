namespace LtlTool.Api.Features.Ai.Narrative.Contracts;

/// <summary>
/// Honest fail-closed <see cref="INarrativeService"/> used when <c>AI:NarrativeEnabled</c> is off
/// (or the real <c>NarrativeService</c> is not registered). Always reports "no narrative available"
/// as a definitive, non-transient result — <c>(null, Cached: false)</c> — which the endpoint maps
/// to <c>404 plan-not-found</c>. Never calls an AI provider, so a fresh clone / CI / the demo
/// never reach OpenAI. This is the intended default when the feature flag is off; the real
/// <see cref="INarrativeService"/> implementation (PR #149) takes over when the flag is on.
/// </summary>
public sealed class NullNarrativeService : INarrativeService
{
    public Task<(NarrativeResponse? Response, bool Cached)> GenerateAsync(string planId, CancellationToken ct)
        => Task.FromResult<(NarrativeResponse?, bool)>((null, false));
}
