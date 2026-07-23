using System.Security.Claims;
using LtlTool.Api.Options;
using LtlTool.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Me;

/// <summary>
/// Read-only diagnostic endpoint that returns the caller's authenticated identity, the raw
/// claims on their bearer token, and the exact policy decision the API would make for the
/// <see cref="AccessPolicies.AllowedEmailDomain"/> policy.
///
/// <para>
/// Guarded by <c>RequireAuthenticatedUser</c> only (not by <c>AllowedEmailDomain</c>) — so a
/// caller who is authenticated but currently blocked from the rest of the API can still hit
/// this to see <b>why</b> they are blocked. Returns 401 when unauthenticated; never returns
/// 403 by design (the whole point is to help diagnose 403s elsewhere).
/// </para>
///
/// <para>
/// The response only exposes claims that already live on the caller's own token — nothing
/// server-side. No secrets, no state changes, no external calls. Safe to ship in every
/// environment.
/// </para>
/// </summary>
[ApiController]
[Route("api/me/whoami")]
[Authorize(Policy = AccessPolicies.AuthenticatedOnly)] // deliberately skips AllowedEmailDomain — diagnostic reads own claims only
public sealed class WhoAmIController(IOptions<AccessPolicyOptions> policyOptions) : ControllerBase
{
    private readonly AccessPolicyOptions _policy = policyOptions.Value;

    [HttpGet]
    public IActionResult Get()
    {
        var preferredUsername = User.FindFirstValue("preferred_username");
        var emailClaim = User.FindFirstValue(ClaimTypes.Email) ?? User.FindFirstValue("email");
        var upn = User.FindFirstValue(ClaimTypes.Upn) ?? User.FindFirstValue("upn");

        // Resolve exactly as the AllowedEmailDomain policy does — across v1.0/v2.0 token shapes and
        // ASP.NET's mapped/unmapped claim types — so this diagnostic reflects the real decision.
        var effectiveEmail = CallerIdentity.ResolveEmail(User);
        var extractedDomain = CallerIdentity.ExtractDomain(effectiveEmail);

        var allowed = _policy.NormalizedEmailDomains;
        var domainMatches = extractedDomain is not null &&
            allowed.Any(d => string.Equals(d, extractedDomain, StringComparison.OrdinalIgnoreCase));
        var wouldAllow = allowed.Length == 0 || domainMatches;

        return Ok(new
        {
            isAuthenticated = User.Identity?.IsAuthenticated ?? false,
            authScheme = User.Identity?.AuthenticationType,
            identityName = User.Identity?.Name,
            preferredUsername,
            emailClaim,
            upn,
            effectiveEmail,
            policyChecks = new
            {
                allowedEmailDomains = allowed,
                extractedDomain,
                domainMatches,
                wouldAllow,
                explanation = wouldAllow
                    ? "This caller would satisfy the AllowedEmailDomain policy."
                    : allowed.Length == 0
                        ? "AllowedEmailDomains is empty — but wouldAllow is still true; if you are seeing 403s elsewhere, it is NOT this policy."
                        : extractedDomain is null
                            ? "No preferred_username/email/upn claim was parseable to a domain. The token is missing the email claim the policy reads."
                            : $"Extracted domain '{extractedDomain}' is not in the AllowedEmailDomains allow-list.",
            },
            claims = User.Claims.Select(c => new { c.Type, c.Value }).ToArray(),
        });
    }
}
