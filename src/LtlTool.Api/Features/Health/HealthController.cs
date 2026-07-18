using LtlTool.Api.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Health;

[ApiController]
[Route("api/health")]
public sealed class HealthController(IOptions<AccessPolicyOptions> accessPolicy) : ControllerBase
{
    private readonly AccessPolicyOptions _accessPolicy = accessPolicy.Value;

    /// <summary>
    /// Anonymous liveness probe. Also surfaces <c>authMode</c> so ops has a second
    /// independent check that a UAT/prod deployment is not in demo mode \u2014 the
    /// startup banner is the first, the health field is the second.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    public IActionResult Get() =>
        Ok(new
        {
            status = "ok",
            utc = DateTimeOffset.UtcNow,
            authMode = _accessPolicy.Mode.ToString(),
        });
}
