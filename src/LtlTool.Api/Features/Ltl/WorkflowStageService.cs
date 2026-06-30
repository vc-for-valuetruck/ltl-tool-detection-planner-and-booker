namespace LtlTool.Api.Features.Ltl;

/// <summary>
/// Places a normalized load in the dispatcher workflow (Search → Match → Assign → Bill) and
/// derives the single recommended next action plus the evidence backing it. This is a pure
/// decision-support projection over already-normalized signals (assignment, status, billing
/// readiness, exceptions, missing data, visibility): it invents nothing and writes nothing back
/// to Alvys.
///
/// <para>
/// Search is the universal entry point — every load is searchable — so a load's <i>current</i>
/// stage is one of the forward stages: it needs capacity (<see cref="WorkflowStage.Match"/>), is
/// committed and in motion (<see cref="WorkflowStage.Assign"/>), is delivered and awaiting billing
/// (<see cref="WorkflowStage.Bill"/>), or is invoiced/closed (<see cref="WorkflowStage.Billed"/>).
/// A stage can additionally be <c>blocked</c> when a gap (missing planning data, a billing-blocking
/// exception, a failed tracking share) prevents it advancing.
/// </para>
/// </summary>
public sealed class WorkflowStageService
{
    /// <summary>Missing fields that prevent a load from being matched to capacity at all.</summary>
    private static readonly MissingDataFlag[] MatchBlockingFields =
    [
        MissingDataFlag.Origin, MissingDataFlag.Destination,
        MissingDataFlag.PickupDate, MissingDataFlag.Equipment,
    ];

    /// <summary>
    /// Evaluate the workflow position from normalized signals. <paramref name="delivered"/> and
    /// <paramref name="hasRevenue"/> are passed explicitly so this service stays a pure function of
    /// already-derived values rather than re-parsing the raw Alvys load.
    /// </summary>
    public WorkflowState Evaluate(
        AssignmentState assignment,
        string status,
        BillingReadinessResult billing,
        IReadOnlyList<LtlExceptionFlag> exceptions,
        IReadOnlyList<MissingDataFlag> missing,
        VisibilityContext visibility,
        bool delivered,
        bool hasRevenue)
    {
        // Billing-blocking exceptions are blockers in every stage — surface their messages.
        var blockingExceptions = exceptions.Where(e => e.BlocksBilling).Select(e => e.Message).ToList();

        // ---- Billed: invoiced/closed, terminal. ----
        if (billing.IsAlreadyInvoiced)
        {
            return Build(
                WorkflowStage.Billed, stepIndex: 4,
                action: "Invoiced — no further dispatcher action required.",
                evidence: ["Invoice on file", StatusEvidence(status)],
                blockers: billing.Risks.Count > 0 ? billing.Risks : []);
            // Unpaid-balance risks (from invoice records) are informational here, not progression blockers.
        }

        // ---- Bill: delivered, not yet invoiced. ----
        if (delivered)
        {
            var blockers = new List<string>();
            blockers.AddRange(blockingExceptions);
            if (!billing.IsReadyToBill)
                blockers.AddRange(billing.Risks);
            blockers.AddRange(VisibilityBlockers(visibility));

            var ready = billing.IsReadyToBill && blockers.Count == 0;
            return Build(
                WorkflowStage.Bill, stepIndex: 4,
                action: ready
                    ? "Cleared to bill — submit the invoice (handled outside this read-only tool)."
                    : "Resolve the billing blockers below before invoicing.",
                evidence: ["Status: Delivered", hasRevenue ? "Rate on file" : "No rate on load"],
                blockers: Dedup(blockers));
        }

        // ---- Assign: capacity committed and in motion, pre-delivery. ----
        if (assignment == AssignmentState.Assigned)
        {
            var blockers = new List<string>();
            blockers.AddRange(VisibilityBlockers(visibility));
            blockers.AddRange(blockingExceptions);

            return Build(
                WorkflowStage.Assign, stepIndex: 3,
                action: blockers.Count > 0
                    ? "Investigate the tracking/exception issues below, then continue toward delivery."
                    : "Dispatched — monitor tracking visibility through delivery.",
                evidence: [$"Status: {status}", "Capacity committed"],
                blockers: Dedup(blockers));
        }

        // ---- Match: unassigned/open — needs capacity. ----
        var matchBlockers = new List<string>();
        var dataGaps = MatchBlockingFields.Where(missing.Contains).ToList();
        if (dataGaps.Count > 0)
            matchBlockers.Add($"Complete load data before matching: {string.Join(", ", dataGaps)}.");
        matchBlockers.AddRange(blockingExceptions);

        var evidence = new List<string> { assignment == AssignmentState.Unassigned ? "Unassigned" : $"Status: {status}" };
        if (hasRevenue) evidence.Add("Rate on file");

        return Build(
            WorkflowStage.Match, stepIndex: 2,
            action: matchBlockers.Count > 0
                ? "Resolve the gaps below, then review recommended matches."
                : "Review recommended matches and stage an internal assignment.",
            evidence: evidence,
            blockers: Dedup(matchBlockers));
    }

    private static IEnumerable<string> VisibilityBlockers(VisibilityContext visibility) =>
        visibility.Evaluated && visibility.HasFailures
            ? ["One or more tracking shares failed — visibility at risk."]
            : [];

    private static string StatusEvidence(string status) =>
        string.IsNullOrWhiteSpace(status) ? "Status: Unknown" : $"Status: {status}";

    private static List<string> Dedup(List<string> items) =>
        items.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();

    private static WorkflowState Build(
        WorkflowStage stage, int stepIndex, string action,
        IReadOnlyList<string> evidence, IReadOnlyList<string> blockers) => new()
    {
        Stage = stage,
        StageLabel = stage.ToString(),
        StepIndex = stepIndex,
        RecommendedAction = action,
        Evidence = evidence.Where(e => !string.IsNullOrWhiteSpace(e)).ToList(),
        IsBlocked = blockers.Count > 0,
        Blockers = blockers,
    };
}
