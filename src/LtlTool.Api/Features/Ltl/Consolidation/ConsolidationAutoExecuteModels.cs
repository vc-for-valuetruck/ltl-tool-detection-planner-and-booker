using LtlTool.Api.Features.Integrations.Alvys.Writeback;

namespace LtlTool.Api.Features.Ltl.Consolidation;

/// <summary>
/// Request to auto-execute a consolidation plan's five Alvys click-card operations on the
/// dispatcher's behalf (see <c>docs/AUTO_CONSOLIDATE_SPEC.md</c> §2). Carries the same
/// <see cref="ConsolidationPlanRequest"/> the read-only preview uses (so the server rebuilds and
/// re-validates the plan rather than trusting a client-supplied one) plus the trip identifiers the
/// preview model does not carry: the parent trip id and, per sibling, the child trip id and the
/// waypoint's CompanyId / sequence. Nothing here is a secret — the acting-user session token is
/// acquired server-side by <see cref="Integrations.Alvys.IAlvysInternalTokenProvider"/>.
/// </summary>
public sealed class ConsolidationAutoExecuteRequest
{
    /// <summary>The plan to (re)build and execute. The server rebuilds it against live Alvys.</summary>
    public ConsolidationPlanRequest Plan { get; set; } = new();

    /// <summary>Alvys trip id of the parent load (the trip the LTL references + waypoints land on).</summary>
    public string ParentTripId { get; set; } = "";

    /// <summary>Parent load number — transported to Alvys as the <c>main_load_id</c> reference.</summary>
    public string? ParentLoadNumber { get; set; }

    /// <summary>Siblings to fold into the parent, each with its child trip + waypoint identity.</summary>
    public List<ConsolidationAutoExecuteSibling> Siblings { get; set; } = [];

    /// <summary>The acting dispatcher's Alvys user id, whose session token authorises the writes.</summary>
    public string? ActingUserId { get; set; }

    /// <summary>Optional dispatcher justification recorded on every operation's audit row.</summary>
    public string? Reason { get; set; }

    /// <summary>Optional override of the confirm-and-Undo countdown; advisory metadata only (§4).</summary>
    public int? UndoWindowSeconds { get; set; }
}

/// <summary>
/// One sibling in an auto-execute request: the plan-level load id (cross-checked against the rebuilt
/// plan so writes stay corridor-bounded, §9) plus the trip/waypoint identifiers the write path needs.
/// </summary>
public sealed class ConsolidationAutoExecuteSibling
{
    /// <summary>Alvys child trip id whose dispatch miles are zeroed and references set.</summary>
    public string ChildTripId { get; set; } = "";

    /// <summary>Plan-level load id — must match a sibling in the rebuilt plan.</summary>
    public string LoadId { get; set; } = "";

    /// <summary>Human-facing load number, for step labels only.</summary>
    public string? LoadNumber { get; set; }

    /// <summary>Alvys company/location id the parent-trip waypoint links to.</summary>
    public string CompanyId { get; set; } = "";

    /// <summary>Zero-based position of the new waypoint in the parent trip's stop sequence.</summary>
    public int Sequence { get; set; }

    /// <summary>Optional scheduled arrival for the waypoint.</summary>
    public DateTimeOffset? ScheduledAt { get; set; }
}

/// <summary>Outcome of a single step in the auto-execute sequence.</summary>
public enum ConsolidationAutoExecuteStepStatus
{
    /// <summary>The gate was closed — the step was never dispatched (the whole plan is blocked).</summary>
    NotDispatched,

    /// <summary>An earlier step failed; this step was skipped (halt-on-first-failure, §5).</summary>
    Skipped,

    /// <summary>The internal-API write executed and Alvys accepted it.</summary>
    Confirmed,

    /// <summary>The internal-API write was attempted but failed; the sequence halts here.</summary>
    Failed,

    /// <summary>An idempotent replay of an already-recorded operation (no new upstream write).</summary>
    DuplicateReplay,

    /// <summary>The idempotency key was reused with a different payload; the sequence halts here.</summary>
    Conflict,
}

/// <summary>
/// One step in the auto-execute report. Mirrors a single Alvys operation dispatch (or the reason it
/// was not dispatched). Carries no secrets and no raw Alvys error body — only the disposition, a
/// dispatcher-safe message, and the durable outbox record id for traceability.
/// </summary>
public sealed class ConsolidationAutoExecuteStep
{
    public required int Order { get; init; }
    public required string OperationCode { get; init; }
    public required string Title { get; init; }

    /// <summary>Human description of the target (e.g. "parent trip T-1", "child trip T-9").</summary>
    public required string Target { get; init; }

    /// <summary>Plan-level sibling load id this step acts on, when it is a per-sibling step.</summary>
    public string? SiblingLoadId { get; init; }

    public required ConsolidationAutoExecuteStepStatus Status { get; init; }

    /// <summary>The underlying <see cref="AlvysOperationDisposition"/> name, for audit parity.</summary>
    public required string Disposition { get; init; }

    public bool Executed { get; init; }
    public IReadOnlyList<string> Blockers { get; init; } = [];
    public string? Message { get; init; }

    /// <summary>Durable outbox record id, when a record was created.</summary>
    public string? RecordId { get; init; }
}

/// <summary>
/// The auto-execute report. When the gate is closed <see cref="Dispatched"/> is false, every step is
/// <see cref="ConsolidationAutoExecuteStepStatus.NotDispatched"/> and <see cref="Blockers"/> explains
/// why — no recorder call was made. When the gate is open the steps carry the per-operation result and
/// <see cref="Executed"/> is true only when every step confirmed.
/// </summary>
public sealed class ConsolidationAutoExecuteResponse
{
    public required string PreviewId { get; init; }
    public required string CorridorCode { get; init; }

    /// <summary>Echoes the readiness kill switch (§3.5) so the UI can reconcile its toggle.</summary>
    public required bool AutoConsolidateEnabled { get; init; }

    /// <summary>True when the gate was open and at least one operation was sent to the recorder.</summary>
    public required bool Dispatched { get; init; }

    /// <summary>True only when every step confirmed and none failed/halted.</summary>
    public required bool Executed { get; init; }

    public required int UndoWindowSeconds { get; init; }

    /// <summary>Reasons the plan could not be dispatched; empty when the gate was open.</summary>
    public IReadOnlyList<string> Blockers { get; init; } = [];

    public required IReadOnlyList<ConsolidationAutoExecuteStep> Steps { get; init; }
}

/// <summary>
/// Session-status probe for the Plan Detail toggle (§4). Reports whether auto-execute is enabled and
/// whether the acting dispatcher currently has a usable Alvys session — <b>never</b> the token itself.
/// <see cref="ExpiresInSeconds"/> stays null: the internal token provider exposes no expiry
/// introspection in this phase.
/// </summary>
public sealed class ConsolidationAutoExecuteSessionStatus
{
    public required bool AutoConsolidateEnabled { get; init; }
    public required bool HasValidSession { get; init; }
    public int? ExpiresInSeconds { get; init; }

    /// <summary>Dispatcher-safe explanation when there is no valid session (never a raw error body).</summary>
    public string? Reason { get; init; }
}
