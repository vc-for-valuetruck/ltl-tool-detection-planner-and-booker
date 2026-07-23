namespace LtlTool.Api.Features.Integrations.Alvys.Writeback;

/// <summary>
/// Controls whether Alvys write-oriented operations are audit-only, dry-run/simulation only, or
/// eligible for execution against a non-production Alvys <b>sandbox</b>.
///
/// <para>
/// The default is <see cref="Disabled"/> so a fresh clone, CI and any production-like deployment
/// never writes back to Alvys. Sandbox execution must be turned on deliberately and is further
/// gated by configuration (a recognised sandbox environment + a sandbox base URL + credentials) so
/// that flipping the mode alone can never reach a live/production tenant.
/// </para>
/// </summary>
public enum AlvysWritebackMode
{
    /// <summary>
    /// No writeback. Operations are recorded locally for audit only; payloads are never built for
    /// execution and nothing is sent to Alvys. This is the safe default.
    /// </summary>
    Disabled = 0,

    /// <summary>
    /// Dry-run only. Payloads are constructed and validated so the dispatcher can preview exactly
    /// what <i>would</i> be sent, but the operation is never executed against Alvys.
    /// </summary>
    Simulation = 1,

    /// <summary>
    /// Eligible for execution against the Alvys <b>sandbox</b> — still gated by operation support
    /// and sandbox configuration readiness. Never targets a production tenant.
    /// </summary>
    Sandbox = 2,
}

/// <summary>
/// Server-side configuration for the Alvys writeback boundary. Bound from the
/// <c>Alvys:Writeback</c> configuration section (env vars <c>Alvys__Writeback__*</c> /
/// <c>ALVYS_WRITEBACK_*</c>). All values default to the safest possible posture.
///
/// <para>
/// This object never carries OAuth credentials — those stay on <see cref="AlvysOptions"/> and are
/// read server-side only. Writeback reuses the same credentials but a distinct
/// <see cref="SandboxBaseUrl"/> so sandbox traffic is physically separated from the read
/// source-of-truth host.
/// </para>
/// </summary>
public sealed class AlvysWriteOptions
{
    public const string SectionName = "Alvys:Writeback";

    /// <summary>
    /// Environment names accepted as a non-production sandbox. <see cref="AlvysWritebackMode.Sandbox"/>
    /// is only honoured when <see cref="Environment"/> matches one of these (case-insensitive).
    /// </summary>
    public static readonly string[] RecognisedSandboxEnvironments =
        ["sandbox", "uat", "staging", "test"];

    /// <summary>
    /// Writeback posture. Defaults to <see cref="AlvysWritebackMode.Disabled"/> so nothing is ever
    /// written to Alvys unless an operator explicitly opts in.
    /// </summary>
    public AlvysWritebackMode Mode { get; set; } = AlvysWritebackMode.Disabled;

    /// <summary>
    /// Operator-declared environment label (e.g. <c>sandbox</c>, <c>uat</c>). Sandbox execution is
    /// refused unless this is one of <see cref="RecognisedSandboxEnvironments"/>, so an unset or
    /// production label can never be executed against.
    /// </summary>
    public string Environment { get; set; } = "";

    /// <summary>
    /// Host root for the Alvys <b>sandbox</b> API (no version, no trailing slash required). Kept
    /// distinct from <see cref="AlvysOptions.ApiBaseUrl"/> so sandbox writeback never points at the
    /// production read host. Required for sandbox execution.
    /// </summary>
    public string SandboxBaseUrl { get; set; } = "";

    /// <summary>
    /// Separate arm switch for the carrier-invoice attach operation. Defaults to <c>false</c>: an
    /// unmatched <c>PaymentType</c> silently defaults to 30-day terms in Alvys, so this write must be
    /// enabled deliberately in addition to sandbox mode. Turning sandbox mode on is never enough on
    /// its own for carrier-invoice.
    /// </summary>
    public bool EnableCarrierInvoice { get; set; } = false;

    /// <summary>
    /// Whitelist of <c>PaymentType</c> values that may be sent on a carrier-invoice attach. Alvys
    /// accepts any string and silently falls back to 30-day terms for unknown values, so a value that
    /// is not on this list is rejected before dispatch. Empty by default (any PaymentType is refused
    /// until an operator declares the accepted set).
    /// </summary>
    public string[] AllowedCarrierInvoicePaymentTypes { get; set; } = [];

    /// <summary>True when a carrier-invoice PaymentType is on the operator-declared whitelist.</summary>
    public bool IsAllowedPaymentType(string? paymentType) =>
        !string.IsNullOrWhiteSpace(paymentType)
        && AllowedCarrierInvoicePaymentTypes.Contains(paymentType.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when the configured environment is a recognised non-production sandbox.
    /// </summary>
    public bool IsRecognisedSandboxEnvironment =>
        RecognisedSandboxEnvironments.Contains(
            Environment.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when a sandbox base URL is configured and is not obviously a production host.
    /// </summary>
    public bool HasSandboxBaseUrl =>
        !string.IsNullOrWhiteSpace(SandboxBaseUrl)
        && !SandboxBaseUrl.Contains("integrations.alvys.com", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------------------------------
    // Production-execution gate (docs/ltl-tool.md "What has to be true before production writeback
    // is enabled", item 3).
    //
    // This is the SECOND, independent gate the production-writeback decision record requires —
    // deliberately separate from the sandbox gate above so that enabling production can never be a
    // side effect of relaxing a sandbox check. Every field defaults to the refusing value. Even with
    // all fields set, this object only governs *authorisation*; it does not itself perform a write.
    //
    // IMPORTANT: the live production HTTP dispatch path is intentionally NOT wired to this gate by
    // the change that introduced it. Authorising production is one deliberate, reviewable step;
    // wiring the transport to a live freight tenant is a second step that a human must perform in a
    // reviewed PR after the sign-off row in docs/ltl-tool.md is filled. See wave1_writeback_summary.md.
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Master production switch. Defaults to <c>false</c>. On its own it authorises nothing — it is
    /// one of four conditions in <see cref="IsProductionExecutionAllowed"/>, and setting it without
    /// the others fails <see cref="ProductionEnablementIsCoherent"/> at startup.
    /// </summary>
    public bool AllowProduction { get; set; } = false;

    /// <summary>
    /// Host root for the real Alvys <b>production</b> API. Required for production execution and,
    /// unlike <see cref="SandboxBaseUrl"/>, it MUST be the production host — see
    /// <see cref="HasProductionBaseUrl"/>. Empty by default.
    /// </summary>
    public string ProductionBaseUrl { get; set; } = "";

    /// <summary>
    /// Machine-readable assertion that the human sign-off row in <c>docs/ltl-tool.md</c> has been
    /// filled and the writeback PR approved. Kept separate from <see cref="AllowProduction"/> so that
    /// neither flag alone can authorise a production write. Defaults to <c>false</c>.
    /// </summary>
    public bool SignOffConfirmed { get; set; } = false;

    /// <summary>
    /// Per-operation production allowlist. Even with the gate open, only operation codes named here
    /// are authorised for production, because each operation has a different blast radius
    /// (docs/ltl-tool.md item 2 — approval is per-operation, never blanket). Empty by default.
    /// </summary>
    public string[] ProductionApprovedOperations { get; set; } = [];

    /// <summary>
    /// True only when a production base URL is configured AND it is the real Alvys production host.
    /// This is the deliberate <b>inverse</b> of <see cref="HasSandboxBaseUrl"/>: the sandbox path
    /// rejects the production host, the production path requires it, so the two hosts can never be
    /// confused for one another.
    /// </summary>
    public bool HasProductionBaseUrl =>
        !string.IsNullOrWhiteSpace(ProductionBaseUrl)
        && ProductionBaseUrl.Contains("integrations.alvys.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the named operation is on the per-operation production allowlist.</summary>
    public bool IsProductionApprovedOperation(string? operationCode) =>
        !string.IsNullOrWhiteSpace(operationCode)
        && ProductionApprovedOperations.Contains(operationCode.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every condition that must hold before a single production write is authorised for the named
    /// operation. All inputs default to the refusing value, so this is <c>true</c> only after an
    /// operator has deliberately opened all four gates.
    /// </summary>
    public bool IsProductionExecutionAllowed(string? operationCode) =>
        AllowProduction
        && SignOffConfirmed
        && HasProductionBaseUrl
        && IsProductionApprovedOperation(operationCode);

    /// <summary>Human-readable reasons production execution is refused for the named operation.</summary>
    public IReadOnlyList<string> ProductionBlockers(string? operationCode)
    {
        var blockers = new List<string>();
        if (!AllowProduction)
            blockers.Add("Production writeback is not allowed (Alvys:Writeback:AllowProduction=false).");
        if (!SignOffConfirmed)
            blockers.Add(
                "The human sign-off has not been confirmed (Alvys:Writeback:SignOffConfirmed=false); " +
                "fill the sign-off row in docs/ltl-tool.md and get the PR approved first.");
        if (!HasProductionBaseUrl)
            blockers.Add(
                "No production base URL is configured, or it is not the real Alvys production host " +
                "(Alvys:Writeback:ProductionBaseUrl).");
        if (!IsProductionApprovedOperation(operationCode))
            blockers.Add(
                $"Operation '{operationCode}' is not on the per-operation production allowlist " +
                "(Alvys:Writeback:ProductionApprovedOperations).");
        return blockers;
    }

    /// <summary>
    /// Startup coherence guard: <see cref="AllowProduction"/> may not be set without ALSO confirming
    /// sign-off, pointing at the real production host, and approving at least one operation. A
    /// half-configured production enablement therefore fails closed at startup rather than silently
    /// arming a partial production posture.
    /// </summary>
    public bool ProductionEnablementIsCoherent =>
        !AllowProduction
        || (SignOffConfirmed && HasProductionBaseUrl && ProductionApprovedOperations.Length > 0);
}
