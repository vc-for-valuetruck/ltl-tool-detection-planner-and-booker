namespace LtlTool.Api.Features.Ai.Narrative.Contracts;

// Shared contract between the narrative endpoint (PR #150), the frontend component (PR #151),
// and the NarrativeService implementation (PR #149). Do not drift the shape without
// coordinating changes across all three.

/// <summary>
/// Generates a short, citation-backed narrative ("why review / what to verify / next action")
/// for a consolidation plan. Read-only against Alvys — the narrative is derived from
/// already-normalized plan data, never a new Alvys read/write.
/// </summary>
public interface INarrativeService
{
    /// <summary>
    /// Resolves the plan and produces its narrative.
    /// <para>
    /// Returns <c>(Response, Cached)</c>:
    /// <list type="bullet">
    /// <item>non-null <c>Response</c> — the generated (or cache-served) narrative; <c>Cached</c>
    /// indicates whether it was served from the cache.</item>
    /// <item>null <c>Response</c> — the narrative could not be produced. The endpoint peeks the
    /// <c>AI:NarrativeEnabled</c> kill switch to distinguish an unknown/unresolvable plan
    /// (404 plan-not-found) from an upstream AI outage (503 ai-unavailable).</item>
    /// </list>
    /// </para>
    /// </summary>
    Task<(NarrativeResponse? Response, bool Cached)> GenerateAsync(string planId, CancellationToken ct);
}

/// <summary>
/// The narrative payload returned to the SPA. <c>Citations</c> reference the plan facts the
/// narrative is grounded in so the recommendation stays explainable and audit-friendly.
/// </summary>
public sealed record NarrativeResponse(string WhyReview, string WhatToVerify, string NextAction, string[] Citations);
