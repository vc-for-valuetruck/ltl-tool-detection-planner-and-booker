namespace LtlTool.Api.Security;

public static class AccessPolicies
{
    public const string AllowedEmailDomain = "AllowedEmailDomain";

    /// <summary>
    /// Service-to-service policy for the Yard→LTL ingestion endpoint. Satisfied by a caller
    /// presenting the configured Entra app role (<c>roles</c> claim) or delegated scope
    /// (<c>scp</c> claim). This is deliberately separate from <see cref="AllowedEmailDomain"/>:
    /// Yard posts with its managed identity's client-credentials token, which carries no user
    /// email/UPN, so the email-domain policy would reject it.
    /// </summary>
    public const string YardEventIngest = "YardEventIngest";

    /// <summary>
    /// Diagnostic policy: requires authentication only, with no email-domain check. Used by
    /// <c>WhoAmIController</c> so a caller currently blocked by <see cref="AllowedEmailDomain"/>
    /// can still see <b>why</b> — the whole point of the diagnostic is to help diagnose 403s
    /// elsewhere. Read-only; exposes only the caller's own token claims. Not appropriate for
    /// any endpoint that reads or writes application data.
    /// </summary>
    public const string AuthenticatedOnly = "AuthenticatedOnly";
}
