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
    /// </summary>
    public string[] AllowedEmailDomains { get; set; } = [];
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
