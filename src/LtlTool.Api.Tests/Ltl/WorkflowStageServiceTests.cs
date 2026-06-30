using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the workflow-stage projection places a load correctly in the Search → Match → Assign →
/// Bill flow, recommends a sensible next action, blocks on real gaps, and never fabricates signals.
/// </summary>
public sealed class WorkflowStageServiceTests
{
    private static readonly WorkflowStageService Service = new();

    private static BillingReadinessResult Billing(
        bool readyToBill = false, bool alreadyInvoiced = false, params string[] risks) => new()
    {
        IsReadyToBill = readyToBill,
        IsAlreadyInvoiced = alreadyInvoiced,
        Risks = risks,
    };

    private static WorkflowState Evaluate(
        AssignmentState assignment, string status, BillingReadinessResult billing,
        bool delivered, bool hasRevenue = true,
        IReadOnlyList<LtlExceptionFlag>? exceptions = null,
        IReadOnlyList<MissingDataFlag>? missing = null,
        VisibilityContext? visibility = null) =>
        Service.Evaluate(
            assignment, status, billing, exceptions ?? [], missing ?? [],
            visibility ?? VisibilityContext.NotEvaluated, delivered, hasRevenue);

    [Fact]
    public void Unassigned_load_is_at_match_and_not_blocked_when_data_is_complete()
    {
        var state = Evaluate(AssignmentState.Unassigned, "Open", Billing(), delivered: false);

        Assert.Equal(WorkflowStage.Match, state.Stage);
        Assert.Equal(2, state.StepIndex);
        Assert.False(state.IsBlocked);
        Assert.Contains("recommended matches", state.RecommendedAction);
        Assert.Contains("Unassigned", state.Evidence);
    }

    [Fact]
    public void Match_is_blocked_when_critical_planning_data_is_missing()
    {
        var state = Evaluate(
            AssignmentState.Unassigned, "Open", Billing(), delivered: false,
            missing: [MissingDataFlag.Origin, MissingDataFlag.PickupDate]);

        Assert.Equal(WorkflowStage.Match, state.Stage);
        Assert.True(state.IsBlocked);
        Assert.Contains(state.Blockers, b => b.Contains("Origin") && b.Contains("PickupDate"));
    }

    [Fact]
    public void Assigned_in_transit_load_is_at_assign_step_three()
    {
        var state = Evaluate(AssignmentState.Assigned, "In Transit", Billing(), delivered: false);

        Assert.Equal(WorkflowStage.Assign, state.Stage);
        Assert.Equal(3, state.StepIndex);
        Assert.False(state.IsBlocked);
        Assert.Contains("monitor tracking", state.RecommendedAction);
    }

    [Fact]
    public void Assign_is_blocked_when_a_tracking_share_failed()
    {
        var visibility = new VisibilityContext
        {
            Evaluated = true,
            Events = [new VisibilityEventView { Direction = "Outbound", IsFailure = true }],
        };

        var state = Evaluate(
            AssignmentState.Assigned, "In Transit", Billing(), delivered: false, visibility: visibility);

        Assert.Equal(WorkflowStage.Assign, state.Stage);
        Assert.True(state.IsBlocked);
        Assert.Contains(state.Blockers, b => b.Contains("tracking shares failed"));
    }

    [Fact]
    public void Delivered_ready_load_is_at_bill_and_cleared()
    {
        var state = Evaluate(
            AssignmentState.Assigned, "Delivered", Billing(readyToBill: true), delivered: true);

        Assert.Equal(WorkflowStage.Bill, state.Stage);
        Assert.Equal(4, state.StepIndex);
        Assert.False(state.IsBlocked);
        Assert.Contains("Cleared to bill", state.RecommendedAction);
    }

    [Fact]
    public void Delivered_load_with_billing_risk_is_blocked_at_bill()
    {
        var state = Evaluate(
            AssignmentState.Assigned, "Delivered",
            Billing(readyToBill: false, risks: "Delivered load has no proof-of-delivery document."),
            delivered: true);

        Assert.Equal(WorkflowStage.Bill, state.Stage);
        Assert.True(state.IsBlocked);
        Assert.Contains(state.Blockers, b => b.Contains("proof-of-delivery"));
        Assert.Contains("Resolve the billing blockers", state.RecommendedAction);
    }

    [Fact]
    public void Invoiced_load_is_at_billed_terminal_and_not_blocked()
    {
        var state = Evaluate(
            AssignmentState.Assigned, "Invoiced", Billing(alreadyInvoiced: true), delivered: true);

        Assert.Equal(WorkflowStage.Billed, state.Stage);
        Assert.False(state.IsBlocked);
        Assert.Contains("no further dispatcher action", state.RecommendedAction);
    }

    [Fact]
    public void Billing_blocking_exception_blocks_progression_in_any_stage()
    {
        var exceptions = new[]
        {
            new LtlExceptionFlag { Code = "MISSING_RATE", Message = "No customer rate — revenue at risk.", BlocksBilling = true },
        };

        var state = Evaluate(
            AssignmentState.Unassigned, "Open", Billing(), delivered: false,
            exceptions: exceptions, hasRevenue: false);

        Assert.True(state.IsBlocked);
        Assert.Contains(state.Blockers, b => b.Contains("revenue at risk"));
    }

    [Fact]
    public void Normalizer_folds_workflow_onto_summary()
    {
        var load = new AlvysLoad
        {
            Id = "L1",
            Status = "Delivered",
            CustomerRate = 1000m,
            Weight = 8000m,
            CustomerId = "C1",
            ActualDeliveryAt = new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero),
        };

        var summary = LtlTestFactory.Normalizer().Normalize(load);

        // Delivered, uninvoiced → Bill stage with a concrete recommended action.
        Assert.Equal(WorkflowStage.Bill, summary.Workflow.Stage);
        Assert.False(string.IsNullOrWhiteSpace(summary.Workflow.RecommendedAction));
    }
}
