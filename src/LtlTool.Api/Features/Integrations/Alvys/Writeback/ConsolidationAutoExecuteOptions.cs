namespace LtlTool.Api.Features.Integrations.Alvys.Writeback;

/// <summary>
/// Server-side configuration for the consolidation auto-execute orchestrator (the "Execute now"
/// action that walks Poornima's five click-card operations through the existing Alvys internal-API
/// write boundary on the dispatcher's behalf). Bound from the <c>Ltl:Writeback:AutoConsolidate</c>
/// configuration section (env vars <c>Ltl__Writeback__AutoConsolidate__*</c> /
/// <c>LTL_WRITEBACK_AUTOCONSOLIDATE_*</c>). See <c>docs/AUTO_CONSOLIDATE_SPEC.md</c> §3.1.
///
/// <para>
/// This flag gates <b>only</b> the orchestrator. It never bypasses the per-operation arm switches on
/// <see cref="AlvysInternalApiOptions"/> or the sandbox posture on <see cref="AlvysWriteOptions"/> —
/// both the flag AND the existing internal-API gates must be true before any operation dispatches.
/// It defaults to <see cref="Enabled"/> = <c>false</c> so a fresh clone, CI and any production-like
/// deployment never offer auto-execution. Doubling as the runtime kill switch (§3.5), flipping it to
/// <c>false</c> stops the orchestrator from starting any new operation.
/// </para>
/// </summary>
public sealed class ConsolidationAutoExecuteOptions
{
    public const string SectionName = "Ltl:Writeback:AutoConsolidate";

    /// <summary>
    /// Master switch for the auto-execute orchestrator. Defaults to <c>false</c>. When false the
    /// orchestrator refuses to dispatch and the readiness endpoint reports
    /// <c>AutoConsolidateEnabled=false</c>.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Seconds the UI holds the confirm-and-Undo countdown before the first Alvys call fires
    /// (§4). Purely advisory metadata for the SPA — the backend never delays a dispatch on it.
    /// </summary>
    public int UndoWindowSeconds { get; set; } = 8;
}
