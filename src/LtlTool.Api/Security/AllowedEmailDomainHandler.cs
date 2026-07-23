using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using LtlTool.Api.Options;

namespace LtlTool.Api.Security;

public sealed class AllowedEmailDomainRequirement : IAuthorizationRequirement;

/// <summary>
/// Allows the request only when the caller's email/UPN belongs to one of the
/// configured <see cref="AccessPolicyOptions.AllowedEmailDomains"/>. An empty
/// allow-list permits any authenticated user (useful for first-run/local UAT).
/// </summary>
public sealed class AllowedEmailDomainHandler(IOptions<AccessPolicyOptions> options)
    : AuthorizationHandler<AllowedEmailDomainRequirement>
{
    private readonly AccessPolicyOptions _options = options.Value;

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, AllowedEmailDomainRequirement requirement)
    {
        var allowed = _options.NormalizedEmailDomains;
        if (allowed.Length == 0)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var email = context.User.FindFirstValue("preferred_username")
            ?? context.User.FindFirstValue(ClaimTypes.Email)
            ?? context.User.FindFirstValue("upn");

        var domain = email?.Split('@', StringSplitOptions.RemoveEmptyEntries) is { Length: 2 } parts
            ? parts[1]
            : null;

        if (domain is not null &&
            allowed.Any(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
