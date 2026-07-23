using System.Security.Claims;

namespace LtlTool.Api.Security;

/// <summary>
/// Resolves the caller's email / UPN and its domain from a validated <see cref="ClaimsPrincipal"/>,
/// independent of Entra token version and of ASP.NET's inbound claim-type mapping.
///
/// <para>
/// The UAT 403 root cause (2026-07-23): the API's Entra registration issues <b>v1.0</b> access
/// tokens (<c>aud=api://&lt;client-id&gt;</c>, no <c>requestedAccessTokenVersion=2</c>). v1.0 tokens
/// carry the caller identity in <c>upn</c> / <c>unique_name</c> and have <b>no</b>
/// <c>preferred_username</c> claim (that is v2.0-only). Compounding this, ASP.NET's
/// <c>JwtBearerOptions.MapInboundClaims</c> remaps some short JWT claim names to the long
/// <c>ClaimTypes.*</c> WS-* URIs (<c>upn</c>→<see cref="ClaimTypes.Upn"/>, <c>email</c>→
/// <see cref="ClaimTypes.Email"/>) — so reading the short name alone can miss the value.
/// The previous handler read only <c>preferred_username</c> → <see cref="ClaimTypes.Email"/>
/// → <c>"upn"</c>, all of which can be absent on a v1.0 token, yielding a null domain and a 403
/// for a legitimately authenticated user.
/// </para>
///
/// <para>
/// This resolver checks every claim type the identity can realistically arrive under — in both
/// short and mapped forms — and returns the first value that parses to an <c>user@domain</c>
/// address. It only ever reads claims already present on the caller's own validated token; it
/// performs no I/O and trusts no external input.
/// </para>
/// </summary>
public static class CallerIdentity
{
    /// <summary>
    /// Claim types that can carry an email-/UPN-shaped caller identity, ordered most-specific
    /// first. Covers v2.0 (<c>preferred_username</c>), v1.0 (<c>upn</c>, <c>unique_name</c>),
    /// explicit <c>email</c>, and the mapped WS-* URIs ASP.NET rewrites some of these to.
    /// </summary>
    public static readonly string[] EmailClaimTypes =
    [
        "preferred_username",
        ClaimTypes.Upn,   // http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn (mapped "upn")
        "upn",
        ClaimTypes.Email, // http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress (mapped "email")
        "email",
        "unique_name",    // v1.0 tokens
        ClaimTypes.Name,  // mapped "unique_name"/"name" — accepted only when it parses to user@domain
    ];

    /// <summary>
    /// The caller's effective email/UPN: the first configured claim whose value parses to a
    /// single <c>user@domain</c> address. Returns <c>null</c> when no such claim is present.
    /// </summary>
    public static string? ResolveEmail(ClaimsPrincipal user)
    {
        foreach (var claimType in EmailClaimTypes)
        {
            var value = user.FindFirstValue(claimType);
            if (ExtractDomain(value) is not null)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>The lower-invariant domain of <see cref="ResolveEmail"/>, or <c>null</c>.</summary>
    public static string? ResolveDomain(ClaimsPrincipal user) => ExtractDomain(ResolveEmail(user));

    /// <summary>The domain part of a single <c>user@domain</c> value, lower-cased; else <c>null</c>.</summary>
    public static string? ExtractDomain(string? email) =>
        email?.Split('@', StringSplitOptions.RemoveEmptyEntries) is { Length: 2 } parts
            ? parts[1].ToLowerInvariant()
            : null;
}
