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
}
