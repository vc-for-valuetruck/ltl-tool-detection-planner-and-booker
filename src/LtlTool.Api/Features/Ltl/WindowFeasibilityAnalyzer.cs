using LtlTool.Api.Features.Integrations.Alvys;

namespace LtlTool.Api.Features.Ltl;

/// <summary>
/// The verdict of assessing whether a candidate's current Alvys trip commitment clears in time for
/// a load's pickup. Deterministic and grounded only in Alvys trip/stop data — there is no HOS, ELD
/// or live-GPS input, so when no pickup instant or no active-trip signal exists the assessment stays
/// <see cref="MatchFactorStatus.Unavailable"/> (excluded from the score denominator, never a penalty).
/// </summary>
public sealed class WindowFeasibilityAssessment
{
    /// <summary>Not-evaluated: no pickup instant, or the active-trip search was not issued.</summary>
    public static readonly WindowFeasibilityAssessment NotEvaluated = new();

    /// <summary>True only when a pickup instant was known and active trips were actually queried.</summary>
    public bool Evaluated { get; init; }

    /// <summary>
    /// True when the active-trip search was bounded/truncated, so the <i>absence</i> of a commitment
    /// cannot be asserted as "free". A positive conflict is still reported under truncation; only the
    /// clean "no conflicting trip" verdict is withheld (stays unavailable) when truncated.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>The candidate's current committed trip, when one was found on the active-trip search.</summary>
    public string? CommittedTripId { get; init; }

    /// <summary>When the candidate's current commitment is expected to clear (latest stop window end).</summary>
    public DateTimeOffset? ClearsAt { get; init; }

    /// <summary>The load pickup instant the commitment was compared against.</summary>
    public DateTimeOffset? PickupAt { get; init; }

    /// <summary>True only when a commitment was found that clears strictly after the pickup instant.</summary>
    public bool Infeasible { get; init; }
}

/// <summary>
/// One bounded active-trip sweep shared across a candidate set: which committed trip (if any) carries
/// each truck/trailer/driver id, and when each of those trips clears. Built once per match request in
/// <see cref="MatchService"/> and turned into a per-candidate <see cref="WindowFeasibilityAssessment"/>.
/// </summary>
public sealed class TripCommitmentContext
{
    public static readonly TripCommitmentContext NotEvaluated = new();

    public bool Evaluated { get; init; }
    public bool Truncated { get; init; }
    public DateTimeOffset? PickupAt { get; init; }

    /// <summary>Truck/trailer/driver id → the committed trip id carrying it.</summary>
    public IReadOnlyDictionary<string, string> TripByEquipmentOrDriver { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Committed trip id → when it clears (null when its stops were not fetched/windowed).</summary>
    public IReadOnlyDictionary<string, DateTimeOffset?> ClearsAtByTrip { get; init; }
        = new Dictionary<string, DateTimeOffset?>();
}

/// <summary>
/// Pure, synchronous interpreter of a candidate's Alvys trip commitment against a load's pickup
/// instant. The (bounded) trip search + stop-detail fetch live in <see cref="MatchService"/>; this
/// only classifies the timing and never asserts a candidate is free from data it was not given.
/// </summary>
public sealed class WindowFeasibilityAnalyzer
{
    /// <summary>
    /// Assess feasibility. <paramref name="evaluated"/> reflects whether the active-trip search was
    /// actually issued for a known pickup instant. <paramref name="committedTripId"/>/<paramref name="clearsAt"/>
    /// describe the candidate's current commitment (both null when the candidate is on no active trip).
    /// </summary>
    public WindowFeasibilityAssessment Assess(
        DateTimeOffset? pickupAt,
        bool evaluated,
        bool truncated,
        string? committedTripId,
        DateTimeOffset? clearsAt)
    {
        if (!evaluated || pickupAt is null)
            return WindowFeasibilityAssessment.NotEvaluated;

        // Candidate is on a committed trip whose clearing time we know: compare directly.
        if (committedTripId is { Length: > 0 } && clearsAt is { } clears)
        {
            return new WindowFeasibilityAssessment
            {
                Evaluated = true,
                Truncated = truncated,
                CommittedTripId = committedTripId,
                ClearsAt = clears,
                PickupAt = pickupAt,
                Infeasible = clears > pickupAt.Value,
            };
        }

        // Candidate is on a committed trip but its clearing time is unknown (stop detail not fetched
        // / no windowed stop): we cannot assert feasibility either way → stay unavailable.
        if (committedTripId is { Length: > 0 })
            return WindowFeasibilityAssessment.NotEvaluated;

        // Candidate is on no active trip. Only a complete (non-truncated) search lets us treat the
        // absence of a commitment as genuinely free; otherwise stay unavailable rather than guess.
        return truncated
            ? WindowFeasibilityAssessment.NotEvaluated
            : new WindowFeasibilityAssessment { Evaluated = true, PickupAt = pickupAt };
    }
}
