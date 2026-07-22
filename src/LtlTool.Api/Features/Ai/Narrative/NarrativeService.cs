using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LtlTool.Api.Features.Ai.Narrative.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ai.Narrative;

/// <summary>
/// Produces a short, sourced narrative for a consolidation plan via Azure OpenAI, with a kill
/// switch, a 10-minute in-memory cache keyed by plan content, and fail-closed semantics.
///
/// <para>
/// Tuple-discriminator contract (relied on by the endpoint PR #150 for 404 vs 503 branching):
/// <list type="bullet">
/// <item>When <c>Response</c> is non-null, <c>Cached</c> means "served from the cache" (true) vs
/// "freshly generated" (false).</item>
/// <item>When <c>Response</c> is null, the flag is a failure discriminator:
/// <c>(null, false)</c> → the plan was not found / the feature is off (endpoint → <b>404</b>);
/// <c>(null, true)</c> → the plan was found but the model failed or returned unusable output
/// (endpoint → <b>503</b>).</item>
/// </list>
/// There is no collision: a cache hit always carries a non-null response, so <c>(null, true)</c>
/// unambiguously means an AI failure.
/// </para>
///
/// <para>
/// Behavioral contract (see <c>docs/phase-2-sprint-1-narrative.md</c>):
/// <list type="bullet">
/// <item>Kill switch <c>AI:NarrativeEnabled</c> off (default) → returns <c>(null, false)</c> before any work.</item>
/// <item>Unknown/blank plan id → returns <c>(null, false)</c>.</item>
/// <item>Cache hit → returns the cached response with <c>Cached = true</c> and never calls the model.</item>
/// <item>Any model exception → logged as a Warning with a correlation id, returns <c>(null, true)</c>.</item>
/// <item>Model output missing any of the four required fields → returns <c>(null, true)</c> (no partial fill).</item>
/// </list>
/// Read-only against Alvys — no writeback. The cache is in-memory, so this slice adds no EF DbSet.
/// </para>
/// </summary>
public sealed class NarrativeService(
    INarrativePlanSource planSource,
    INarrativeChatClient chatClient,
    IMemoryCache cache,
    IOptionsMonitor<AiFeatureFlags> flags,
    ILogger<NarrativeService> logger) : INarrativeService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        // No indentation; property order follows declaration order → deterministic canonical JSON.
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly JsonSerializerOptions ModelJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private const string SystemPrompt =
        "You are a freight-operations analyst assistant. You are given a JSON consolidation plan " +
        "built from a trucking TMS. Explain, for a dispatch/accounting reviewer, why this plan " +
        "warrants review, what to verify, and the single best next action.\n" +
        "Rules:\n" +
        "- Respond with ONE JSON object and nothing else, with EXACTLY these keys: " +
        "whyReview (string), whatToVerify (string), nextAction (string), citations (array of strings).\n" +
        "- citations MUST contain at least one specific plan field name you relied on " +
        "(e.g. \"combinedRevenuePerMile\", \"blockers\", \"rpmWarningStatus\").\n" +
        "- NEVER invent values, loads, customers, or numbers that are not present in the plan JSON. " +
        "If a value is null/missing, say it is missing rather than guessing.\n" +
        "- Keep each string concise (one or two sentences).";

    public async Task<(NarrativeResponse? Response, bool Cached)> GenerateAsync(
        string planId, CancellationToken ct)
    {
        // Kill switch first — fail closed before any plan fetch or model call.
        if (!flags.CurrentValue.NarrativeEnabled)
        {
            return (null, false);
        }

        var correlationId = Guid.NewGuid().ToString("n");

        // Stage 1 — resolve the plan. A failure here is "not found" (→ 404), never an AI failure.
        NarrativePlanPayload? payload;
        try
        {
            payload = await planSource.GetPlanPayloadAsync(planId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Narrative skipped: plan '{PlanId}' could not be resolved (correlationId {CorrelationId}).",
                planId, correlationId);
            return (null, false);
        }

        if (payload is null)
        {
            logger.LogWarning(
                "Narrative skipped: plan '{PlanId}' was not found (correlationId {CorrelationId}).",
                planId, correlationId);
            return (null, false);
        }

        var planJson = JsonSerializer.Serialize(payload, CanonicalJsonOptions);
        var cacheKey = $"narrative:{planId}:{ComputeHash(planJson)}";

        if (cache.TryGetValue(cacheKey, out NarrativeResponse? cached) && cached is not null)
        {
            return (cached, true);
        }

        // Stage 2 — the model. From here on the plan exists, so any failure/unusable output is an
        // AI failure and surfaces as (null, true) → 503, never (null, false).
        try
        {
            var content = await chatClient.CompleteJsonAsync(SystemPrompt, planJson, ct);
            var response = ParseAndValidate(content);
            if (response is null)
            {
                logger.LogWarning(
                    "Narrative discarded: model output was empty or missing required fields for plan " +
                    "'{PlanId}' (correlationId {CorrelationId}).",
                    planId, correlationId);
                return (null, true);
            }

            cache.Set(cacheKey, response, CacheTtl);
            return (response, false);
        }
        catch (Exception ex)
        {
            // Fail closed on any model failure (transport, credential, serialization). Never bubble.
            logger.LogWarning(ex,
                "Narrative generation failed for plan '{PlanId}' (correlationId {CorrelationId}); " +
                "returning no narrative.",
                planId, correlationId);
            return (null, true);
        }
    }

    private static string ComputeHash(string canonicalJson)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static NarrativeResponse? ParseAndValidate(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var json = StripCodeFences(content);

        ModelOutput? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ModelOutput>(json, ModelJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (parsed is null) return null;
        if (string.IsNullOrWhiteSpace(parsed.WhyReview)) return null;
        if (string.IsNullOrWhiteSpace(parsed.WhatToVerify)) return null;
        if (string.IsNullOrWhiteSpace(parsed.NextAction)) return null;
        if (parsed.Citations is null) return null;

        var citations = parsed.Citations
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .ToArray();
        if (citations.Length == 0) return null;

        return new NarrativeResponse(
            parsed.WhyReview.Trim(),
            parsed.WhatToVerify.Trim(),
            parsed.NextAction.Trim(),
            citations);
    }

    private static string StripCodeFences(string content)
    {
        var json = content.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = json.IndexOf('\n');
            if (firstNewline >= 0) json = json[(firstNewline + 1)..];
            var lastFence = json.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0) json = json[..lastFence];
        }
        return json.Trim();
    }

    private sealed class ModelOutput
    {
        [JsonPropertyName("whyReview")]
        public string? WhyReview { get; set; }

        [JsonPropertyName("whatToVerify")]
        public string? WhatToVerify { get; set; }

        [JsonPropertyName("nextAction")]
        public string? NextAction { get; set; }

        [JsonPropertyName("citations")]
        public List<string>? Citations { get; set; }
    }
}
