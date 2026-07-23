using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Integrations.Alvys.Writeback;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.Consolidation;

/// <summary>
/// Orchestrates the consolidation auto-execute flow (<c>docs/AUTO_CONSOLIDATE_SPEC.md</c>): it
/// rebuilds the plan against live Alvys, applies the gate, and — only when every gate passes —
/// sequences the five §2 operations through the <b>existing</b>
/// <see cref="IAlvysOperationRecorder"/> / internal-API write boundary. It introduces no new writer.
/// </summary>
public interface IConsolidationAutoExecuteService
{
    /// <summary>
    /// Rebuilds + gates + (when open) executes the plan. When the gate is closed the returned report
    /// lists every step as <see cref="ConsolidationAutoExecuteStepStatus.NotDispatched"/> and the
    /// recorder is never called.
    /// </summary>
    Task<ConsolidationAutoExecuteResponse> ExecuteAsync(
        ConsolidationAutoExecuteRequest request, string ownerId, CancellationToken ct = default);

    /// <summary>Reports whether auto-execute is enabled and the acting dispatcher has a valid session.</summary>
    Task<ConsolidationAutoExecuteSessionStatus> GetSessionStatusAsync(
        string actingUserId, CancellationToken ct = default);
}

/// <inheritdoc cref="IConsolidationAutoExecuteService"/>
public sealed class ConsolidationAutoExecuteService : IConsolidationAutoExecuteService
{
    private readonly ConsolidationPlanService _plans;
    private readonly IAlvysReadinessService _readiness;
    private readonly IAlvysOperationRecorder _recorder;
    private readonly IAlvysInternalTokenProvider _tokenProvider;
    private readonly ConsolidationAutoExecuteOptions _options;
    private readonly ILogger<ConsolidationAutoExecuteService> _logger;

    /// <summary>
    /// The sign-off / live-support gate predicate. The DI/production path binds this to the registry
    /// (every internal consolidation op is <see cref="AlvysLiveSupport.Unsupported"/> in Phase 1a, so
    /// no live write is ever reachable). A test-only constructor overrides it so the §6.2 integration
    /// tests can exercise the dispatch path without flipping the shared static registry.
    /// </summary>
    private readonly Func<AlvysWriteOperationDescriptor?, bool> _isLiveSupported;

    public ConsolidationAutoExecuteService(
        ConsolidationPlanService plans,
        IAlvysReadinessService readiness,
        IAlvysOperationRecorder recorder,
        IAlvysInternalTokenProvider tokenProvider,
        IOptions<ConsolidationAutoExecuteOptions> options,
        ILogger<ConsolidationAutoExecuteService> logger)
        : this(plans, readiness, recorder, tokenProvider, options, logger,
            op => op?.LiveSupport == AlvysLiveSupport.Supported)
    {
    }

    /// <summary>
    /// Test seam: lets integration tests drive the dispatch path while the shared static registry
    /// keeps every op Unsupported. Never used by DI — production always uses the registry default.
    /// </summary>
    public ConsolidationAutoExecuteService(
        ConsolidationPlanService plans,
        IAlvysReadinessService readiness,
        IAlvysOperationRecorder recorder,
        IAlvysInternalTokenProvider tokenProvider,
        IOptions<ConsolidationAutoExecuteOptions> options,
        ILogger<ConsolidationAutoExecuteService> logger,
        Func<AlvysWriteOperationDescriptor?, bool> isLiveSupported)
    {
        _plans = plans;
        _readiness = readiness;
        _recorder = recorder;
        _tokenProvider = tokenProvider;
        _options = options.Value;
        _logger = logger;
        _isLiveSupported = isLiveSupported;
    }

    public async Task<ConsolidationAutoExecuteResponse> ExecuteAsync(
        ConsolidationAutoExecuteRequest request, string ownerId, CancellationToken ct = default)
    {
        // Rebuild + re-validate the plan against live Alvys — the client's plan is never trusted.
        var plan = await _plans.BuildAsync(request.Plan, ct);
        var status = _readiness.GetStatus();

        var planned = ConsolidationAutoExecuteSequence.Build(request);
        var blockers = CollectBlockers(request, plan, status, planned);
        var undoWindow = request.UndoWindowSeconds ?? _options.UndoWindowSeconds;

        if (blockers.Count > 0)
        {
            // Gate closed: the recorder is NEVER called. Every step is reported as NotDispatched so
            // the UI can render the full sequence it would run, annotated with the blocking reasons.
            var notDispatched = planned
                .Select(p => Step(p, ConsolidationAutoExecuteStepStatus.NotDispatched,
                    AlvysOperationDisposition.Blocked, executed: false, blockers, message: null, recordId: null))
                .ToList();

            _logger.LogInformation(
                "Consolidation metric: auto_execute_blocked corridor={Corridor} steps={StepCount} "
                + "blockers={BlockerCount} autoConsolidateEnabled={Enabled}",
                plan.CorridorCode, planned.Count, blockers.Count, status.AutoConsolidateEnabled);

            return new ConsolidationAutoExecuteResponse
            {
                PreviewId = plan.PreviewId,
                CorridorCode = plan.CorridorCode,
                AutoConsolidateEnabled = status.AutoConsolidateEnabled,
                Dispatched = false,
                Executed = false,
                UndoWindowSeconds = undoWindow,
                Blockers = blockers,
                Steps = notDispatched,
            };
        }

        // Gate open: dispatch the sequence, halting on the first failure/conflict (no auto-rollback, §5).
        var steps = new List<ConsolidationAutoExecuteStep>();
        var halted = false;

        foreach (var p in planned)
        {
            if (halted)
            {
                steps.Add(Step(p, ConsolidationAutoExecuteStepStatus.Skipped,
                    AlvysOperationDisposition.Blocked, executed: false, [],
                    message: "Skipped after an earlier step failed.", recordId: null));
                continue;
            }

            var result = await _recorder.RecordExecuteAsync(ownerId, p.OperationCode, p.Request, ct);
            var stepStatus = MapStatus(result);
            steps.Add(Step(p, stepStatus, result.Outcome.Disposition, result.Outcome.Executed,
                result.Outcome.Blockers, result.Outcome.Message, result.Record?.Id));

            if (stepStatus is ConsolidationAutoExecuteStepStatus.Failed
                or ConsolidationAutoExecuteStepStatus.Conflict)
            {
                halted = true;
            }
        }

        var executed = !halted && steps.All(s =>
            s.Status is ConsolidationAutoExecuteStepStatus.Confirmed
                     or ConsolidationAutoExecuteStepStatus.DuplicateReplay);

        _logger.LogInformation(
            "Consolidation metric: auto_execute corridor={Corridor} steps={StepCount} "
            + "confirmed={Confirmed} halted={Halted}",
            plan.CorridorCode, steps.Count,
            steps.Count(s => s.Status == ConsolidationAutoExecuteStepStatus.Confirmed), halted);

        return new ConsolidationAutoExecuteResponse
        {
            PreviewId = plan.PreviewId,
            CorridorCode = plan.CorridorCode,
            AutoConsolidateEnabled = status.AutoConsolidateEnabled,
            Dispatched = true,
            Executed = executed,
            UndoWindowSeconds = undoWindow,
            Blockers = [],
            Steps = steps,
        };
    }

    public async Task<ConsolidationAutoExecuteSessionStatus> GetSessionStatusAsync(
        string actingUserId, CancellationToken ct = default)
    {
        var enabled = _readiness.GetStatus().AutoConsolidateEnabled;

        if (!enabled)
        {
            return new ConsolidationAutoExecuteSessionStatus
            {
                AutoConsolidateEnabled = false,
                HasValidSession = false,
                Reason = "Auto-consolidate execution is disabled.",
            };
        }

        if (string.IsNullOrWhiteSpace(actingUserId))
        {
            return new ConsolidationAutoExecuteSessionStatus
            {
                AutoConsolidateEnabled = true,
                HasValidSession = false,
                Reason = "No acting user id was supplied.",
            };
        }

        try
        {
            // Acquire (or reuse a cached) session token to prove a usable dispatcher session exists.
            // The token is NEVER returned — only the boolean; expiry introspection is not available.
            _ = await _tokenProvider.GetSessionTokenAsync(actingUserId, ct);
            return new ConsolidationAutoExecuteSessionStatus
            {
                AutoConsolidateEnabled = true,
                HasValidSession = true,
                ExpiresInSeconds = null,
            };
        }
        catch (InvalidOperationException ex)
        {
            return new ConsolidationAutoExecuteSessionStatus
            {
                AutoConsolidateEnabled = true,
                HasValidSession = false,
                Reason = ex.Message,
            };
        }
    }

    /// <summary>
    /// Every reason the plan cannot be dispatched. Composes the readiness kill switch (§3.5), the
    /// rebuilt plan's own blockers, the trip identifiers the write path needs, a corridor-bound sibling
    /// cross-check (§9), and — the single most important gate (§6.1) — the per-operation sign-off /
    /// live-support check. All three internal ops are <see cref="AlvysLiveSupport.Unsupported"/> today,
    /// so this always blocks live execution until the sign-off rows are filled and the ops are flipped.
    /// </summary>
    private List<string> CollectBlockers(
        ConsolidationAutoExecuteRequest request,
        ConsolidationPlanResponse plan,
        AlvysReadinessStatus status,
        IReadOnlyList<ConsolidationPlannedOperation> planned)
    {
        var blockers = new List<string>();

        if (!status.AutoConsolidateEnabled)
            blockers.Add("Auto-consolidate execution is disabled "
                + "(Ltl:Writeback:AutoConsolidate:Enabled=false, or a production-shaped Alvys host is configured).");

        foreach (var b in plan.Blockers)
            blockers.Add($"Plan blocker: {b}");

        if (string.IsNullOrWhiteSpace(request.ParentTripId))
            blockers.Add("A parent trip id is required to auto-execute the consolidation.");

        if (string.IsNullOrWhiteSpace(request.ActingUserId))
            blockers.Add("An acting user id (the dispatcher's Alvys session) is required to auto-execute.");

        if (request.Siblings.Count == 0)
            blockers.Add("At least one sibling is required to auto-execute a consolidation.");

        // Keep writes corridor-bounded: every sibling the request would mutate must be one the
        // read-only plan actually resolved on the corridor (§9). A sibling not in the plan is refused.
        var planSiblingIds = plan.Siblings.Select(s => s.LoadId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var s in request.Siblings)
        {
            if (string.IsNullOrWhiteSpace(s.ChildTripId))
                blockers.Add($"Sibling '{s.LoadId}' is missing its child trip id.");
            if (string.IsNullOrWhiteSpace(s.CompanyId))
                blockers.Add($"Sibling '{s.LoadId}' is missing the waypoint CompanyId.");
            if (!planSiblingIds.Contains(s.LoadId))
                blockers.Add(
                    $"Sibling '{s.LoadId}' is not part of the resolved consolidation plan and will not be written.");
        }

        // The sign-off / live-support gate. This is what keeps the feature architecturally unreachable
        // in production: no operation dispatches until it is individually live-supported.
        foreach (var code in planned.Select(p => p.OperationCode).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_isLiveSupported(AlvysWriteOperationRegistry.Find(code)))
                blockers.Add($"Operation '{code}' is not signed off for live execution "
                    + "(no confirmed internal-API contract; production gate not implemented).");
        }

        return blockers;
    }

    /// <summary>
    /// Maps a recorder result to a step status. A conflict halts; an internal failure halts; an
    /// internal success (or an idempotent replay of a prior success) proceeds. Anything else means the
    /// operation did not reach a live internal write, which is treated as a failure — never a silent pass.
    /// </summary>
    private static ConsolidationAutoExecuteStepStatus MapStatus(AlvysRecordResult result)
    {
        if (result.Disposition == AlvysRecordDisposition.Conflict)
            return ConsolidationAutoExecuteStepStatus.Conflict;

        return result.Outcome.Disposition switch
        {
            AlvysOperationDisposition.InternalExecuted =>
                result.Disposition == AlvysRecordDisposition.DuplicateReplay
                    ? ConsolidationAutoExecuteStepStatus.DuplicateReplay
                    : ConsolidationAutoExecuteStepStatus.Confirmed,
            AlvysOperationDisposition.InternalFailed => ConsolidationAutoExecuteStepStatus.Failed,
            _ => result.Disposition == AlvysRecordDisposition.DuplicateReplay
                ? ConsolidationAutoExecuteStepStatus.DuplicateReplay
                : ConsolidationAutoExecuteStepStatus.Failed,
        };
    }

    private static ConsolidationAutoExecuteStep Step(
        ConsolidationPlannedOperation p,
        ConsolidationAutoExecuteStepStatus status,
        AlvysOperationDisposition disposition,
        bool executed,
        IReadOnlyList<string> blockers,
        string? message,
        string? recordId) => new()
    {
        Order = p.Order,
        OperationCode = p.OperationCode,
        Title = p.Title,
        Target = p.Target,
        SiblingLoadId = p.SiblingLoadId,
        Status = status,
        Disposition = disposition.ToString(),
        Executed = executed,
        Blockers = blockers,
        Message = message,
        RecordId = recordId,
    };
}
