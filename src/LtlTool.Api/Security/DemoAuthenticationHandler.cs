using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Security;

/// <summary>
/// Constants for the request-time authentication scheme router. The router itself is
/// registered inline in <c>Program.cs</c> via <c>AddPolicyScheme</c>; this class exists
/// only to hold the scheme name so the router registration, its ForwardDefaultSelector,
/// and any downstream code that needs to reference the router (unlikely) all agree on
/// the identifier.
/// </summary>
public static class AuthenticationSchemeRouter
{
    /// <summary>
    /// Name of the policy scheme registered in Program.cs. This is the default scheme
    /// used by the authentication pipeline; at request time the scheme forwards to
    /// either <see cref="DemoAuthenticationHandler.SchemeName"/> or
    /// <see cref="Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme"/>
    /// based on <see cref="Options.AccessPolicyOptions.Mode"/>.
    /// </summary>
    public const string RouterScheme = "AccessPolicyRouter";
}

/// <summary>
/// Demo-mode authentication scheme. Grants every incoming request a synthetic identity
/// so the LTL tool can be walked through end-to-end against **live Alvys read data**
/// without an Entra tenant, MSAL flow, or Azure resources.
///
/// <para>
/// This is a demo-only shim: activated exclusively when <c>AccessPolicy:Mode</c> is
/// <c>Demo</c>. Any other configuration continues to require Entra ID JWT bearer tokens
/// via <c>AddMicrosoftIdentityWebApi</c>. The scheme name is
/// <see cref="SchemeName"/>. It is registered as both the default authentication and
/// challenge scheme when demo mode is on, so <c>[Authorize]</c> attributes flow through
/// this handler.
/// </para>
///
/// <para>
/// The synthetic principal carries <c>preferred_username</c> (the claim
/// <see cref="AllowedEmailDomainHandler"/> reads first) so an empty or configured
/// <see cref="Options.AccessPolicyOptions.AllowedEmailDomains"/> policy still admits
/// the caller.
/// </para>
///
/// <para>
/// <b>Do not enable in any environment that faces the internet or handles real
/// PII/PHI.</b> This grants every caller full API access. It exists so a laptop with
/// Docker Desktop and Alvys credentials can demo the pilot to Value Truck leadership
/// without provisioning Azure UAT.
/// </para>
/// </summary>
public sealed class DemoAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>Scheme identifier registered with <c>AddAuthentication</c>.</summary>
    public const string SchemeName = "Demo";

    /// <summary>Synthetic identity used by demo mode. Matches the domain used in real UAT.</summary>
    private const string DemoUpn = "demo@valuetruck.com";
    private const string DemoName = "Demo Dispatcher";

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            // Claims name mirrors what MSAL emits so downstream code (audit trail,
            // AllowedEmailDomainHandler, controller principal reads) sees identical shape.
            new Claim("preferred_username", DemoUpn),
            new Claim(ClaimTypes.Email, DemoUpn),
            new Claim(ClaimTypes.Name, DemoName),
            new Claim("oid", "00000000-0000-0000-0000-000000000001"),
        };

        var identity = new ClaimsIdentity(claims, SchemeName, "preferred_username", ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
