using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Health;

[ApiController]
[Route("api/health")]
public sealed class HealthController(
    IOptions<AccessPolicyOptions> accessPolicy,
    IOptions<AlvysOptions> alvys) : ControllerBase
{
    private readonly AccessPolicyOptions _accessPolicy = accessPolicy.Value;
    private readonly AlvysOptions _alvys = alvys.Value;

    /// <summary>
    /// Anonymous liveness probe. Also surfaces <c>authMode</c> so ops has a second
    /// independent check that a UAT/prod deployment is not in demo mode \u2014 the
    /// startup banner is the first, the health field is the second.
    ///
    /// <para>
    /// <c>alvysProvider</c> / <c>alvysCredentialsPresent</c> are the equivalent guard
    /// for the Alvys data path: a UAT/prod deploy that lands on the <c>Fallback</c>
    /// provider (empty results) or on <c>Live</c> without credentials serves no live
    /// Alvys data, yet <c>/api/health</c> would otherwise still report <c>ok</c>. These
    /// fields let the deploy smoke test and ops catch that misconfiguration without a
    /// signed-in token. No secret is exposed \u2014 only the provider enum and a boolean.
    /// </para>
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    public IActionResult Get() =>
        Ok(new
        {
            status = "ok",
            utc = DateTimeOffset.UtcNow,
            authMode = _accessPolicy.Mode.ToString(),
            alvysProvider = _alvys.Provider.ToString(),
            alvysCredentialsPresent = _alvys.HasCredentials,
        });
}
