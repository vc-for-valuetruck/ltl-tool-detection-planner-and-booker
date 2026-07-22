using LtlTool.Api.Features.Ai.Narrative.Contracts;
using LtlTool.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ai;

/// <summary>
/// AI narrative HTTP surface: a thin, read-only projection over <see cref="INarrativeService"/>
/// (built in #149). Everything here is read-only against Alvys — the endpoint performs no Alvys
/// read or write; the narrative is derived by the service from already-normalized plan data.
///
/// <para>
/// Sits behind the same <see cref="AccessPolicies.AllowedEmailDomain"/> policy as
/// <c>/api/ltl/*</c>, so an unauthenticated caller gets 401 (not 404).
/// </para>
/// </summary>
[ApiController]
[Route("api/ai")]
[Authorize(Policy = AccessPolicies.AllowedEmailDomain)]
[Produces("application/json")]
public sealed class NarrativeEndpoint(
    INarrativeService narrative,
    IOptions<AiOptions> aiOptions) : ControllerBase
{
    /// <summary>
    /// Returns the consolidation-plan narrative for <paramref name="planId"/>.
    /// <list type="bullet">
    /// <item><c>200 OK</c> — narrative found; sets <c>X-Ai-Source: azure-openai</c> and
    /// <c>X-Ai-Cached: true|false</c> from the service's cache signal.</item>
    /// <item><c>404 { reason: "disabled" }</c> — <c>AI:NarrativeEnabled=false</c> (kill switch).
    /// The service is never called in this case.</item>
    /// <item><c>404 { reason: "plan-not-found" }</c> — flag on, service returned
    /// <c>(null, Cached: false)</c>: the plan id could not be resolved.</item>
    /// <item><c>503 { reason: "ai-unavailable" }</c> — flag on, service returned
    /// <c>(null, Cached: true)</c>: the upstream AI generation failed (e.g. OpenAI outage).</item>
    /// </list>
    ///
    /// <para>
    /// When the response is null the <c>Cached</c> tuple value is the discriminator between a
    /// definitive plan-not-found (<c>false</c>) and a transient AI outage (<c>true</c>). This is the
    /// shared convergence contract between this endpoint, <c>INarrativeService</c>, and the
    /// frontend <c>&lt;ai-narrative&gt;</c> component — do not drift it in one place only.
    /// </para>
    /// </summary>
    [HttpGet("consolidation/narrative")]
    [ProducesResponseType(typeof(NarrativeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetNarrative([FromQuery] string planId, CancellationToken ct)
    {
        // Kill switch: never invoke the AI service when the feature is off.
        if (!aiOptions.Value.NarrativeEnabled)
        {
            return NotFound(new { reason = "disabled" });
        }

        var (response, cached) = await narrative.GenerateAsync(planId, ct);

        if (response is not null)
        {
            Response.Headers["X-Ai-Source"] = "azure-openai";
            Response.Headers["X-Ai-Cached"] = cached ? "true" : "false";
            return Ok(response);
        }

        // Null response with the flag on: Cached discriminates a transient AI outage (true) from a
        // definitive unresolved plan (false).
        return cached
            ? StatusCode(StatusCodes.Status503ServiceUnavailable, new { reason = "ai-unavailable" })
            : NotFound(new { reason = "plan-not-found" });
    }
}
