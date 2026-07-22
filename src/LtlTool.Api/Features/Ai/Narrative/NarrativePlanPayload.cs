namespace LtlTool.Api.Features.Ai.Narrative;

/// <summary>
/// The exact, minimal projection of a consolidation plan that is sent to the model — and hashed to
/// key the cache. Kept as its own DTO (rather than reusing <c>ConsolidationPlanResponse</c>) so the
/// "fields sent to the model" contract is explicit and stable: adding a field to the plan response
/// does not silently change the prompt or the cache key. Every value is either a live Alvys read or
/// a static config value already resolved by the plan service — nothing here is fabricated, and
/// missing values stay null (never coerced to 0/false).
/// </summary>
public sealed class NarrativePlanPayload
{
    public required string PlanId { get; init; }
    public required string CorridorCode { get; init; }

    public string? ParentLoadNumber { get; init; }
    public string? ParentCustomerName { get; init; }
    public string? ParentOrigin { get; init; }
    public string? ParentDestination { get; init; }

    public required IReadOnlyList<NarrativePlanSibling> Siblings { get; init; }

    public decimal? CombinedRevenue { get; init; }
    public decimal? LinehaulMiles { get; init; }
    public decimal? DriverLoadedMiles { get; init; }
    public decimal? CombinedDriverTripValue { get; init; }
    public decimal? CombinedRevenuePerMile { get; init; }

    public string? RpmWarningStatus { get; init; }
    public string? RpmWarningMessage { get; init; }

    public string? TrailerFitVerdict { get; init; }

    public required IReadOnlyList<string> Blockers { get; init; }
    public required IReadOnlyList<string> StopSequence { get; init; }
}

/// <summary>One sibling row in the plan payload sent to the model.</summary>
public sealed class NarrativePlanSibling
{
    public required string LoadId { get; init; }
    public string? LoadNumber { get; init; }
    public string? CustomerName { get; init; }
    public string? DestinationLabel { get; init; }
    public decimal? Revenue { get; init; }
    public decimal? WeightLbs { get; init; }
}
