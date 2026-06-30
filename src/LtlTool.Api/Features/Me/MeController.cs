using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using LtlTool.Api.Security;

namespace LtlTool.Api.Features.Me;

/// <summary>
/// Sample protected endpoint. Requires a valid Entra token whose email domain
/// passes the <see cref="AccessPolicies.AllowedEmailDomain"/> policy. Use this to
/// verify the end-to-end auth flow (returns 401 when unauthenticated).
/// </summary>
[ApiController]
[Route("api/me")]
[Authorize(Policy = AccessPolicies.AllowedEmailDomain)]
public sealed class MeController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new
    {
        name = User.Identity?.Name,
        email = User.FindFirstValue("preferred_username") ?? User.FindFirstValue(ClaimTypes.Email),
        claims = User.Claims.Select(c => new { c.Type, c.Value }),
    });
}
