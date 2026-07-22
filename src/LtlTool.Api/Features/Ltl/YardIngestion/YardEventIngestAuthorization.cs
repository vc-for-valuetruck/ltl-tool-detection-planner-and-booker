using System.Security.Claims;
using LtlTool.Api.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.YardIngestion;

/// <summary>Authorization requirement for the service-to-service Yard ingestion endpoint.</summary>
public sealed class YardEventIngestRequirement : IAuthorizationRequirement;

/// <summary>
/// Grants access to the Yard ingestion endpoint when the caller presents the configured Entra
/// <b>app role</b> (<c>roles</c> claim) or delegated <b>scope</b> (<c>scp</c> claim). Yard's managed
/// identity is granted the app role on the LTL API app registration, so its client-credentials token
/// carries the role — no shared secret is introduced.
///
/// <para>Two escape hatches keep local/UAT usable without an Entra tenant:</para>
/// <list type="bullet">
///   <item>When <c>AccessPolicy:Mode</c> is <see cref="AccessPolicyMode.Demo"/>, the synthetic demo
///   identity is admitted (mirrors how every other endpoint is walkable in demo mode).</item>
///   <item>When both <see cref="YardIngestionOptions.RequiredAppRole"/> and
///   <see cref="YardIngestionOptions.RequiredScope"/> are empty, any authenticated caller is admitted
///   (the check is disabled by configuration).</item>
/// </list>
/// </summary>
public sealed class YardEventIngestHandler(
    IOptions<YardIngestionOptions> ingestionOptions,
    IOptions<AccessPolicyOptions> accessPolicyOptions)
    : AuthorizationHandler<YardEventIngestRequirement>
{
    private readonly YardIngestionOptions _ingestion = ingestionOptions.Value;
    private readonly AccessPolicyOptions _accessPolicy = accessPolicyOptions.Value;

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, YardEventIngestRequirement requirement)
    {
        // Demo mode: every request already carries the synthetic identity; admit it so the pipeline
        // is walkable end-to-end without an Entra tenant. Never enabled in a public-facing deployment.
        if (_accessPolicy.Mode == AccessPolicyMode.Demo)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var requireRole = !string.IsNullOrWhiteSpace(_ingestion.RequiredAppRole);
        var requireScope = !string.IsNullOrWhiteSpace(_ingestion.RequiredScope);

        // Neither configured → the check is disabled; any authenticated caller passes.
        if (!requireRole && !requireScope)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (requireRole && HasAppRole(context.User, _ingestion.RequiredAppRole!))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (requireScope && HasScope(context.User, _ingestion.RequiredScope!))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    /// <summary>App roles arrive as one or more <c>roles</c> claims (client-credentials or delegated).</summary>
    private static bool HasAppRole(ClaimsPrincipal user, string role) =>
        user.FindAll("roles").Any(c => string.Equals(c.Value, role, StringComparison.OrdinalIgnoreCase))
        || user.FindAll(ClaimTypes.Role).Any(c => string.Equals(c.Value, role, StringComparison.OrdinalIgnoreCase));

    /// <summary>The <c>scp</c> claim is a single space-delimited string of granted scopes.</summary>
    private static bool HasScope(ClaimsPrincipal user, string scope) =>
        user.FindAll("scp")
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Any(s => string.Equals(s, scope, StringComparison.OrdinalIgnoreCase));
}
