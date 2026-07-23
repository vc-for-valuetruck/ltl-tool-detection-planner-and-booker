using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LtlTool.Api.Options;

namespace LtlTool.Api.Security;

public sealed class AllowedEmailDomainRequirement : IAuthorizationRequirement;

/// <summary>
/// Allows the request only when the caller's email/UPN belongs to one of the
/// configured <see cref="AccessPolicyOptions.AllowedEmailDomains"/>. An empty
/// allow-list permits any authenticated user (useful for first-run/local UAT).
///
/// <para>
/// The caller identity is resolved via <see cref="CallerIdentity"/>, which reads every claim
/// type a v1.0 or v2.0 token can carry it under (mapped and unmapped). This is deliberate: the
/// API issues v1.0 tokens, which have no <c>preferred_username</c> — see
/// <see cref="CallerIdentity"/> for the full 403 root-cause note. On denial the handler logs the
/// claim <b>types</b> present (never their values) so a future 403 can be triaged from logs alone.
/// </para>
/// </summary>
public sealed class AllowedEmailDomainHandler(
    IOptions<AccessPolicyOptions> options,
    ILogger<AllowedEmailDomainHandler> logger)
    : AuthorizationHandler<AllowedEmailDomainRequirement>
{
    private readonly AccessPolicyOptions _options = options.Value;
    private readonly ILogger<AllowedEmailDomainHandler> _logger = logger;

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, AllowedEmailDomainRequirement requirement)
    {
        var allowed = _options.NormalizedEmailDomains;
        if (allowed.Length == 0)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var domain = CallerIdentity.ResolveDomain(context.User);

        if (domain is not null && allowed.Contains(domain, StringComparer.OrdinalIgnoreCase))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // Denied. Log the claim TYPES present (no values → no PII/token leakage) plus whether a
        // domain was resolvable, so a 403 can be root-caused from logs without a token dump.
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            var claimTypes = context.User.Claims.Select(c => c.Type).Distinct();
            _logger.LogWarning(
                "AllowedEmailDomain policy DENIED. Resolvable domain: {ResolvedDomain}. "
                + "Allow-list: {Allowed}. Authenticated: {IsAuthenticated}. Claim types on token: {ClaimTypes}.",
                domain ?? "(none)",
                string.Join(",", allowed),
                context.User.Identity?.IsAuthenticated ?? false,
                string.Join(",", claimTypes));
        }

        return Task.CompletedTask;
    }
}
