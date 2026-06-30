namespace LtlTool.Api.Features.Ltl.SavedViews;

/// <summary>
/// The shipped enterprise presets shared by every dispatcher. Each preset applies only
/// <b>supported</b> normalized-search filters — it never fabricates data or invents a signal the
/// grid cannot honestly evaluate. The set spans the Search → Match → Assign → Bill workflow so a
/// dispatcher can jump straight to the queue they own.
///
/// <para>
/// Two operationally-interesting signals — per-load tracking-visibility failures and equipment
/// availability conflicts — are deliberately <i>not</i> presets here: both are detail/match-time
/// evaluations (a per-load visibility fetch / equipment-event overlap) that the bulk search grid
/// does not compute, so a preset claiming to filter on them would be misleading. They surface on
/// the Exceptions tab and the per-load match panel instead. "Open exceptions" is the honest grid
/// equivalent and is included below.
/// </para>
/// </summary>
public static class SavedViewPresets
{
    /// <summary>Stable preset definitions, ordered for display (Search → Match → Assign → Bill).</summary>
    public static IReadOnlyList<SavedView> All { get; } =
    [
        Preset(
            "preset-available-ltl", "Available LTL freight",
            "Open LTL/partial loads not yet covered — the matching queue.",
            new SavedViewFilters
            {
                LtlOnly = true,
                Assignment = AssignmentState.Unassigned,
                Stage = WorkflowStage.Match,
                Sort = LtlSortField.PickupDate,
            }),
        Preset(
            "preset-needs-match", "Needs match review",
            "Every load currently sitting in the Match stage, soonest pickup first.",
            new SavedViewFilters { Stage = WorkflowStage.Match, Sort = LtlSortField.PickupDate }),
        Preset(
            "preset-assignment-blocked", "Assignment blocked",
            "Loads that cannot advance until a gap is resolved.",
            new SavedViewFilters { BlockedOnly = true, Sort = LtlSortField.PickupDate }),
        Preset(
            "preset-ready-to-bill", "Ready to bill",
            "Delivered loads cleared for invoicing, readiest first.",
            new SavedViewFilters
            {
                ReadyToBill = true,
                Sort = LtlSortField.BillingReadiness,
                SortDescending = true,
            }),
        Preset(
            "preset-billing-exceptions", "Billing exceptions",
            "Loads with an exception that is blocking clean billing.",
            new SavedViewFilters
            {
                BillingBadge = BillingBadge.ExceptionBlockingBilling,
                Sort = LtlSortField.PickupDate,
            }),
        Preset(
            "preset-missing-billing", "Missing billing data",
            "Loads with a billing-data gap (rate, weight, POD, accessorial review).",
            new SavedViewFilters { MissingBillingData = true, Sort = LtlSortField.PickupDate }),
        Preset(
            "preset-open-exceptions", "Open exceptions",
            "Loads carrying any operational or billing exception, blocking ones first.",
            new SavedViewFilters { ExceptionsOnly = true, Sort = LtlSortField.PickupDate }),
        Preset(
            "preset-invoiced-closed", "Already invoiced / closed",
            "Financially closed loads — invoiced/terminal, newest delivery first.",
            new SavedViewFilters
            {
                Stage = WorkflowStage.Billed,
                Sort = LtlSortField.DeliveryDate,
                SortDescending = true,
            }),
    ];

    private static SavedView Preset(string id, string name, string description, SavedViewFilters filters) =>
        new()
        {
            Id = id,
            Name = name,
            Description = description,
            Filters = filters,
            IsBuiltIn = true,
            OwnerId = null,
        };
}
