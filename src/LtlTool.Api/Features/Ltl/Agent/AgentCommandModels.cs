using System.Text.Json.Serialization;
using LtlTool.Api.Features.Ltl.Consolidation;

namespace LtlTool.Api.Features.Ltl.Agent;

/// <summary>
/// Tool-style schema for one agent command. Deliberately shaped like an LLM function-calling tool
/// definition so a future function-calling layer can advertise the catalog verbatim. Every command
/// in the M4 surface is <see cref="ReadOnly"/> — nothing writes to Alvys.
/// </summary>
public sealed record AgentCommandSchema
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<AgentCommandParameter> Parameters { get; init; }

    /// <summary>Always true in Phase 2 M4: the agent layer never issues Alvys writes.</summary>
    public bool ReadOnly { get; init; } = true;
}

/// <summary>One parameter in a command schema (JSON-schema-lite).</summary>
public sealed record AgentCommandParameter(
    string Name,
    string Type,
    bool Required,
    string Description);

/// <summary>
/// Uniform envelope returned by the dispatcher for every command. <see cref="Ok"/> is false when the
/// command was rejected (unknown/disabled) — validation failures surface as an exception the
/// controller maps to 400, not as an <c>Ok=false</c> body.
/// </summary>
public sealed record AgentCommandResult
{
    public required bool Ok { get; init; }
    public required string Command { get; init; }
    public object? Data { get; init; }
    public string? Error { get; init; }
    public required DateTimeOffset ExecutedAt { get; init; }
}

/// <summary>Thrown by a handler/validator when the request args are invalid. Mapped to HTTP 400.</summary>
public sealed class AgentCommandValidationException(string message) : Exception(message);

/// <summary>Thrown when a command name is not in the catalog. Mapped to HTTP 404.</summary>
public sealed class UnknownAgentCommandException(string command)
    : Exception($"Unknown agent command: '{command}'.")
{
    public string Command { get; } = command;
}

// ---------------------------------------------------------------------------------------------
// Per-command request DTOs. Each handler owns validation of its own DTO.
// ---------------------------------------------------------------------------------------------

/// <summary>Args for <c>list-opportunities</c>.</summary>
public sealed class ListOpportunitiesArgs
{
    public int? Limit { get; set; }
    public int? LookbackDays { get; set; }

    /// <summary>Optional corridor code (e.g. <c>LAREDO_TO_DALLAS</c>) to filter opportunities by lane.</summary>
    public string? Corridor { get; set; }
}

/// <summary>Args for <c>explain-plan</c>.</summary>
public sealed class ExplainPlanArgs
{
    /// <summary>Id of a recorded consolidation audit entry to explain.</summary>
    public string PlanId { get; set; } = "";
}

/// <summary>Args for <c>check-fit</c> — supply a planId OR an explicit load list.</summary>
public sealed class CheckFitArgs
{
    public string? PlanId { get; set; }
    public List<CheckFitLoad>? Loads { get; set; }
    public CheckFitTrailer? Trailer { get; set; }
}

public sealed class CheckFitLoad
{
    public string LoadRef { get; set; } = "";
    public decimal? WeightLbs { get; set; }
    public int? Pallets { get; set; }
    public decimal? Volume { get; set; }
}

public sealed class CheckFitTrailer
{
    public decimal? MaxWeightLbs { get; set; }
    public int? MaxPallets { get; set; }
    public decimal? MaxVolume { get; set; }
}

/// <summary>Args for <c>sequence-stops</c> — supply a planId OR an explicit stop list.</summary>
public sealed class SequenceStopsArgs
{
    public string? PlanId { get; set; }
    public List<SequenceStop>? Stops { get; set; }
}

public sealed class SequenceStop
{
    public string StopRef { get; set; } = "";
    public string? City { get; set; }
    public string? State { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
}

/// <summary>Args for <c>estimate-quote</c>.</summary>
public sealed class EstimateQuoteArgs
{
    public string Origin { get; set; } = "";
    public string Destination { get; set; } = "";
    public decimal WeightLbs { get; set; }
    public QuoteTransportMode? Mode { get; set; }
    public decimal? DistanceMiles { get; set; }
    public bool Perishable { get; set; }
    public bool Hazmat { get; set; }
}

/// <summary>Args for <c>report-incident</c>.</summary>
public sealed class ReportIncidentArgs
{
    public string Origin { get; set; } = "";
    public string Destination { get; set; } = "";
    public int Severity { get; set; }
    public string? Note { get; set; }
}

// ---------------------------------------------------------------------------------------------
// Response DTOs (the shapes returned inside AgentCommandResult.Data).
// ---------------------------------------------------------------------------------------------

/// <summary>Transport mode for a reference quote.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QuoteTransportMode
{
    Truck = 0,
    Rail = 1,
    Air = 2,
}

/// <summary>Input to <see cref="QuoteEstimatorService.Estimate"/>.</summary>
public sealed record QuoteEstimateInput
{
    public required string Origin { get; init; }
    public required string Destination { get; init; }
    public required decimal WeightLbs { get; init; }
    public QuoteTransportMode? Mode { get; init; }
    public decimal? DistanceMiles { get; init; }
    public bool Perishable { get; init; }
    public bool Hazmat { get; init; }
}

/// <summary>
/// A reference-only freight quote. Every dollar/mileage/CO₂ value is an estimate from a static rate
/// card — never an Alvys number. <see cref="Disclaimer"/> and <see cref="DistanceSource"/> keep that
/// explicit for any UI or LLM consumer.
/// </summary>
public sealed record QuoteEstimate
{
    public required string Origin { get; init; }
    public required string Destination { get; init; }
    public required QuoteTransportMode Mode { get; init; }
    public required decimal WeightLbs { get; init; }
    public required decimal DistanceMiles { get; init; }
    public required string DistanceSource { get; init; }
    public required decimal Linehaul { get; init; }
    public required decimal FuelSurcharge { get; init; }
    public required decimal HandlingFee { get; init; }
    public required decimal AccessorialSurcharge { get; init; }
    public required decimal CongestionPremium { get; init; }
    public required decimal SurgeMultiplier { get; init; }
    public required decimal Insurance { get; init; }
    public required decimal TotalCost { get; init; }
    public required decimal Co2Kg { get; init; }

    /// <summary>Corridor risk applied to this quote, when the caller passed the incident store through.</summary>
    public CorridorRisk? Risk { get; init; }

    public string Disclaimer { get; init; } =
        "Reference estimate only — not an Alvys rate, mileage, or booked price.";
}

/// <summary>Result of <c>explain-plan</c>: solver rationale + fit verdict + per-sibling chips.</summary>
public sealed record ExplainPlanResult
{
    public required string PlanId { get; init; }
    public required string CorridorCode { get; init; }
    public required string ParentLoadRef { get; init; }
    public required IReadOnlyList<ExplainPlanSibling> Siblings { get; init; }

    /// <summary>Plain-language summary of why the plan looks the way it does (stops, fit, RPM, blockers).</summary>
    public required string SolverRationale { get; init; }

    /// <summary>Trailer-fit verdict string (Unknown/Fits/DoesNotFit) — Unknown when the fit engine is off.</summary>
    public required string TrailerFitVerdict { get; init; }
    public string? TrailerFitRationale { get; init; }

    public decimal? CombinedRevenue { get; init; }
    public decimal? CombinedDriverRpm { get; init; }
    public bool StopsOptimized { get; init; }
    public required IReadOnlyList<string> Blockers { get; init; }
}

/// <summary>One sibling in an explain-plan result, carrying the reused consolidation fit chips.</summary>
public sealed record ExplainPlanSibling
{
    public required string LoadRef { get; init; }
    public string? CustomerName { get; init; }

    /// <summary>The Lane/Timing/Customer chips reused verbatim from <see cref="ConsolidationCandidateService"/>.</summary>
    public required IReadOnlyList<ConsolidationFactor> Chips { get; init; }
    public required IReadOnlyList<string> Cautions { get; init; }
}

/// <summary>Result of <c>check-fit</c>.</summary>
public sealed record CheckFitResult
{
    public required bool Enabled { get; init; }
    public required string Verdict { get; init; }
    public required string Rationale { get; init; }
    public bool EstimatedFit { get; init; }
    public decimal? TotalWeightLbs { get; init; }
    public decimal? TrailerMaxWeightLbs { get; init; }
    public int? TotalPallets { get; init; }
    public int? TrailerMaxPallets { get; init; }
    public bool CapacityExceeded { get; init; }
    public bool WeightUnknown { get; init; }
    public decimal? LinearFeet { get; init; }
    public decimal? WeightUtilization { get; init; }
    public decimal? CubeUtilization { get; init; }
}

/// <summary>Result of <c>sequence-stops</c>.</summary>
public sealed record SequenceStopsResult
{
    public required IReadOnlyList<string> OrderedStopRefs { get; init; }
    public required bool Optimized { get; init; }
    public required string Rationale { get; init; }
}
