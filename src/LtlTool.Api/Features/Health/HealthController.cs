using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LtlTool.Api.Features.Health;

[ApiController]
[Route("api/health")]
public sealed class HealthController : ControllerBase
{
    /// <summary>Anonymous liveness probe.</summary>
    [HttpGet]
    [AllowAnonymous]
    public IActionResult Get() => Ok(new { status = "ok", utc = DateTimeOffset.UtcNow });
}
