using LtlTool.Api.Features.Integrations.Alvys;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LtlTool.Api.Features.Health;

/// <summary>
/// Anonymous, read-only diagnostic for the Live Alvys data path. It runs a real OAuth2 token
/// acquisition and one tiny <c>loads/search</c> probe and reports the actual HTTP status —
/// making a "Live but Alvys is rejecting our reads" state provable without server-log access.
///
/// <para>Anonymous like <see cref="HealthController"/> so the deploy smoke test and ops can hit it
/// without a signed-in token. It exposes <b>no secret</b>: never the bearer token, and never a
/// success payload (which would carry live operational load data). Non-2xx error snippets are
/// surfaced because that is exactly what pins a 401/403/404 to its root cause.</para>
/// </summary>
[ApiController]
[Route("api/health/alvys")]
public sealed class AlvysHealthController(IAlvysDiagnostics diagnostics) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var report = await diagnostics.ProbeAsync(ct);
        // "ok" only when a live read actually succeeded; "degraded" otherwise so the health workflow
        // and ops catch a Live-but-rejected Alvys path that /api/health would still call "ok".
        var status = report.Provider == nameof(AlvysProvider.Live) && report.Probes.Any(p => p.Ok)
            ? "ok"
            : "degraded";
        return Ok(new
        {
            status,
            utc = DateTimeOffset.UtcNow,
            report,
        });
    }
}
