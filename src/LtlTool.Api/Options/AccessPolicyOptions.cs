namespace LtlTool.Api.Options;

/// <summary>
/// Access-policy configuration for the API. In production this restricts callers by
/// email domain; in <see cref="AccessPolicyMode.Demo"/> mode it swaps Entra JWT bearer
/// for a synthetic identity so the app runs end-to-end without an Entra tenant \u2014
/// see <see cref="Security.DemoAuthenticationHandler"/>.
/// </summary>
public sealed class AccessPolicyOptions
{
    /// <summary>
    /// Auth mode. <see cref="AccessPolicyMode.EntraId"/> (default) uses MSAL JWT bearer.
    /// <see cref="AccessPolicyMode.Demo"/> uses <see cref="Security.DemoAuthenticationHandler"/>
    /// and grants every request a synthetic identity. **Demo mode must never run in a
    /// public-facing environment.**
    /// </summary>
    public AccessPolicyMode Mode { get; set; } = AccessPolicyMode.EntraId;

    /// <summary>
    /// Email domains permitted to access the API. Empty allow-list = any authenticated
    /// caller admitted (useful for local UAT and demo mode).
    ///
    /// <para>
    /// Accepts either config shape and any mix of them:
    /// indexed array entries (<c>AccessPolicy__AllowedEmailDomains__0=valuetruck.com</c>,
    /// <c>__1=valuelogistics.com</c>) <b>and/or</b> a single comma-separated value
    /// (<c>AccessPolicy__AllowedEmailDomains__0=valuetruck.com,valuelogistics.com</c> or a
    /// scalar <c>AccessPolicy__AllowedEmailDomains=valuetruck.com,valuelogistics.com</c>).
    /// Never read this raw array for matching — use <see cref="NormalizedEmailDomains"/>,
    /// which flattens commas, trims, lower-cases, drops blanks, and de-dupes. Matching on the
    /// raw array broke UAT when a comma-separated value landed in a single element (see
    /// <c>AllowedEmailDomainHandler</c>).
    /// </para>
    /// </summary>
    public string[] AllowedEmailDomains { get; set; } = [];

    /// <summary>
    /// The effective allow-list used for matching: every configured entry is split on commas,
    /// trimmed, lower-cased, blanks dropped, and de-duped. An all-blank / empty configuration
    /// normalizes to an empty list, which the handler treats as "admit any authenticated user".
    /// This is what makes both the indexed-array and comma-separated config shapes behave
    /// identically.
    /// </summary>
    public string[] NormalizedEmailDomains =>
        (AllowedEmailDomains ?? [])
            .SelectMany(entry =>
                (entry ?? string.Empty).Split(
                    ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(d => d.ToLowerInvariant())
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}

/// <summary>
/// Authentication surface. Adding a new member here requires wiring it in
/// <c>Program.cs</c>'s auth-scheme registration.
/// </summary>
public enum AccessPolicyMode
{
    /// <summary>Microsoft Entra ID JWT bearer via <c>AddMicrosoftIdentityWebApi</c>.</summary>
    EntraId,

    /// <summary>
    /// Demo shim: grants every request a synthetic identity. Local/demo use only.
    /// </summary>
    Demo,
}
