using LtlTool.Api.Features.Integrations.Alvys.Writeback;

namespace LtlTool.Api.Features.Integrations.Alvys;

/// <summary>
/// Server-side configuration for the Alvys <b>internal</b>-API write path (the endpoints the Alvys
/// web app itself calls). Bound from the <c>Alvys:InternalApi</c> configuration section (env vars
/// <c>Alvys__InternalApi__*</c> / <c>ALVYS_INTERNALAPI_*</c>).
///
/// <para>
/// This path is the Phase-2 consolidation-execution scaffolding described in decision #10 of
/// <c>docs/ALVYS_API_DECISIONS.md</c> (Reuben sync, 2026-07-17). The internal API is
/// <b>observed, not contracted</b> — endpoints can change without notice — so it defaults to
/// <see cref="Enabled"/> = <c>false</c> and every operation is additionally gated behind its own
/// arm switch. Turning the surface on is never enough on its own; each operation must be armed
/// deliberately. This mirrors the "flip the mode alone can never reach a live tenant" posture of the
/// Public-API <see cref="Writeback.AlvysWriteOptions"/>.
/// </para>
///
/// <para>
/// Like <see cref="AlvysOptions"/>, this object carries no secrets. The per-acting-user Auth0
/// session token the internal API requires is acquired and cached by
/// <see cref="IAlvysInternalTokenProvider"/> and is never persisted here.
/// </para>
/// </summary>
public sealed class AlvysInternalApiOptions
{
    public const string SectionName = "Alvys:InternalApi";

    /// <summary>
    /// Master switch for the internal-API write surface. Defaults to <c>false</c> so a fresh clone,
    /// CI and any production-like deployment never dispatches an internal-API write.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Host root for the Alvys internal API (no trailing slash required). Required — and distinct
    /// from the Public-API host — before any internal write can be dispatched.
    /// </summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// Host root for acquiring a per-acting-user session token (the headless-login helper described
    /// in decision #10). Falls back to <see cref="BaseUrl"/> when unset.
    /// </summary>
    public string AuthBaseUrl { get; set; } = "";

    /// <summary>Fallback session-token TTL (seconds) when the auth response omits an expiry.</summary>
    public int TokenTtlSeconds { get; set; } = 3600;

    /// <summary>Per-operation arm switch: create a Waypoint / extended stop on the parent trip.</summary>
    public bool EnableAddExtendedStop { get; set; } = false;

    /// <summary>Per-operation arm switch: zero a child trip's dispatch (loaded) mileage.</summary>
    public bool EnableZeroChildDispatchMiles { get; set; } = false;

    /// <summary>Per-operation arm switch: set trip references (LTL boolean / main load id).</summary>
    public bool EnableSetTripReferences { get; set; } = false;

    /// <summary>True when an internal-API base URL is configured.</summary>
    public bool HasBaseUrl => !string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>Effective auth host — <see cref="AuthBaseUrl"/> when set, otherwise <see cref="BaseUrl"/>.</summary>
    public string EffectiveAuthBaseUrl =>
        string.IsNullOrWhiteSpace(AuthBaseUrl) ? BaseUrl : AuthBaseUrl;

    /// <summary>
    /// Whether the given internal operation is individually armed. An unarmed (or unknown) operation
    /// can never dispatch, even when <see cref="Enabled"/> is true.
    /// </summary>
    public bool IsOperationArmed(AlvysWriteOperationKind kind) => kind switch
    {
        AlvysWriteOperationKind.AddExtendedStop => EnableAddExtendedStop,
        AlvysWriteOperationKind.ZeroChildDispatchMiles => EnableZeroChildDispatchMiles,
        AlvysWriteOperationKind.SetTripReferences => EnableSetTripReferences,
        _ => false,
    };
}
