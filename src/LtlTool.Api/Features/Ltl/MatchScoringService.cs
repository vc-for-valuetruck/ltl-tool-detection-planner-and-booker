using LtlTool.Api.Features.Integrations.Alvys;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl;

/// <summary>
/// A driver/truck/trailer candidate to be scored against a load. Any member may be null — the
/// scorer treats absent equipment/driver data as <i>unavailable</i> factors (not scored)
/// rather than inventing capability.
/// </summary>
public sealed class MatchCandidate
{
    public AlvysDriver? Driver { get; init; }
    public AlvysTruck? Truck { get; init; }
    public AlvysTrailerEquipment? Trailer { get; init; }
}

/// <summary>
/// Deterministic, explainable match scoring. Each factor contributes points out of a configured
/// maximum; the final 0–100 score is <c>earned / availableMax</c>, so factors whose data is
/// unavailable are excluded from the denominator and reported as not-scored instead of dragging
/// the score down. Hard rules (expired credentials, terminated driver, over-capacity) cap the
/// label regardless of points. HOS and historical-performance signals are intentionally always
/// reported as unavailable — the data is not in this slice and is never fabricated.
/// </summary>
public sealed class MatchScoringService(IOptions<LtlOptions> options, TimeProvider clock)
{
    private readonly LtlMatchOptions _match = options.Value.Match;

    public MatchResult Score(
        LtlLoadSummary load, MatchCandidate candidate, EquipmentEventAssessment? events = null)
    {
        var factors = new List<MatchFactor>();
        var disqualifiers = new List<string>();
        var now = clock.GetUtcNow();

        factors.Add(ScoreEquipment(load, candidate.Trailer));
        factors.Add(ScoreWeightCapacity(load, candidate.Trailer, disqualifiers));
        factors.Add(ScoreDriverReadiness(candidate.Driver, now, disqualifiers));
        factors.Add(ScoreFleetAlignment(load, candidate));
        factors.Add(ScoreGeography(load, candidate.Driver));
        factors.Add(ScoreEquipmentEvents(events));
        factors.Add(NotScored("Hours of Service", "HOS data is not available in this slice."));
        factors.Add(NotScored("Historical performance", "Lane/driver history is not available in this slice."));

        var earned = factors.Sum(f => f.Points);
        var availableMax = factors.Sum(f => f.MaxPoints);
        var score = availableMax > 0 ? (int)Math.Round(earned / availableMax * 100) : 0;

        var label = ResolveLabel(score, disqualifiers.Count > 0, availableMax > 0);
        var summary = BuildSummary(label, factors, disqualifiers);

        return new MatchResult
        {
            DriverId = candidate.Driver?.Id,
            DriverName = candidate.Driver?.Name,
            TruckId = candidate.Truck?.Id,
            TruckNumber = candidate.Truck?.TruckNum,
            TrailerId = candidate.Trailer?.Id,
            TrailerNumber = candidate.Trailer?.TrailerNum,
            Label = label,
            LabelText = LabelText(label),
            Score = score,
            Summary = summary,
            Factors = factors,
            Disqualifiers = disqualifiers,
        };
    }

    private MatchFactor ScoreEquipment(LtlLoadSummary load, AlvysTrailerEquipment? trailer)
    {
        const string name = "Equipment match";
        if (load.Equipment.Count == 0 || string.IsNullOrWhiteSpace(trailer?.EquipmentType))
            return NotScored(name, "Load required-equipment or trailer equipment type is unavailable.");

        var trailerType = trailer.EquipmentType!;
        var exact = load.Equipment.Any(e => string.Equals(e, trailerType, StringComparison.OrdinalIgnoreCase));
        if (exact)
            return new MatchFactor { Name = name, Status = MatchFactorStatus.Strong, Detail = $"Trailer is {trailerType}, matching required equipment.", Points = _match.EquipmentWeight, MaxPoints = _match.EquipmentWeight };

        var partial = load.Equipment.Any(e =>
            e.Contains(trailerType, StringComparison.OrdinalIgnoreCase)
            || trailerType.Contains(e, StringComparison.OrdinalIgnoreCase));
        return partial
            ? new MatchFactor { Name = name, Status = MatchFactorStatus.Neutral, Detail = $"Trailer {trailerType} partially matches required {string.Join("/", load.Equipment)}.", Points = _match.EquipmentWeight * 0.5, MaxPoints = _match.EquipmentWeight }
            : new MatchFactor { Name = name, Status = MatchFactorStatus.Weak, Detail = $"Trailer {trailerType} does not match required {string.Join("/", load.Equipment)}.", Points = 0, MaxPoints = _match.EquipmentWeight };
    }

    private MatchFactor ScoreWeightCapacity(
        LtlLoadSummary load, AlvysTrailerEquipment? trailer, List<string> disqualifiers)
    {
        const string name = "Weight capacity";
        var capacity = trailer?.Capacity?.Weight;
        if (load.WeightLbs is null or <= 0 || capacity is null or <= 0)
            return NotScored(name, "Load weight or trailer weight capacity is unavailable.");

        var ratio = load.WeightLbs.Value / capacity.Value;
        if (ratio > 1)
        {
            disqualifiers.Add($"Load weight {load.WeightLbs:n0} lbs exceeds trailer capacity {capacity:n0} lbs.");
            return new MatchFactor { Name = name, Status = MatchFactorStatus.Weak, Detail = $"Over capacity ({ratio:p0}).", Points = 0, MaxPoints = _match.WeightCapacityWeight };
        }

        // Comfortable fit (<=80%) is best; tight fit (80–100%) is neutral.
        return ratio <= 0.8m
            ? new MatchFactor { Name = name, Status = MatchFactorStatus.Strong, Detail = $"Comfortable fit ({ratio:p0} of capacity).", Points = _match.WeightCapacityWeight, MaxPoints = _match.WeightCapacityWeight }
            : new MatchFactor { Name = name, Status = MatchFactorStatus.Neutral, Detail = $"Tight fit ({ratio:p0} of capacity).", Points = _match.WeightCapacityWeight * 0.6, MaxPoints = _match.WeightCapacityWeight };
    }

    private MatchFactor ScoreDriverReadiness(AlvysDriver? driver, DateTimeOffset now, List<string> disqualifiers)
    {
        const string name = "Driver readiness";
        if (driver is null)
            return NotScored(name, "No driver on the candidate.");

        // Terminated / inactive is a hard disqualifier.
        if (driver.TerminatedAt is not null && driver.TerminatedAt <= now)
            disqualifiers.Add("Driver is terminated.");
        else if (driver.IsActive == false)
            disqualifiers.Add("Driver is not active.");

        var notes = new List<string>();
        var penalty = 0.0;
        var hasCredentialData = driver.LicenseExpiresAt is not null || driver.MedicalExpiresAt is not null;

        CheckExpiry(driver.LicenseExpiresAt, "License", now, notes, disqualifiers, ref penalty);
        CheckExpiry(driver.MedicalExpiresAt, "Medical", now, notes, disqualifiers, ref penalty);

        if (!hasCredentialData && driver.IsActive != true && driver.TerminatedAt is null)
            return NotScored(name, "Driver active flag and credential expiries are unavailable.");

        var earned = Math.Max(0, _match.DriverReadinessWeight - penalty);
        var status = penalty == 0
            ? MatchFactorStatus.Strong
            : earned > 0 ? MatchFactorStatus.Neutral : MatchFactorStatus.Weak;
        var detail = notes.Count > 0 ? string.Join(" ", notes) : "Active with valid credentials.";

        return new MatchFactor { Name = name, Status = status, Detail = detail, Points = earned, MaxPoints = _match.DriverReadinessWeight };
    }

    private void CheckExpiry(
        DateTimeOffset? expiry, string label, DateTimeOffset now,
        List<string> notes, List<string> disqualifiers, ref double penalty)
    {
        if (expiry is null) return;

        if (expiry <= now)
        {
            disqualifiers.Add($"{label} expired on {expiry:yyyy-MM-dd}.");
            notes.Add($"{label} expired.");
            penalty += _match.DriverReadinessWeight; // zero out
        }
        else if ((expiry.Value - now).TotalDays <= _match.CredentialExpiryWarningDays)
        {
            notes.Add($"{label} expires {expiry:yyyy-MM-dd}.");
            penalty += _match.DriverReadinessWeight * 0.4;
        }
    }

    private MatchFactor ScoreFleetAlignment(LtlLoadSummary load, MatchCandidate candidate)
    {
        const string name = "Fleet alignment";
        var equipmentSubsidiary = candidate.Trailer?.SubsidiaryId ?? candidate.Truck?.SubsidiaryId;
        var driverSubsidiary = candidate.Driver?.SubsidiaryId;
        var subsidiary = equipmentSubsidiary ?? driverSubsidiary;

        // OfficeId is the closest load-side subsidiary signal available.
        if (string.IsNullOrWhiteSpace(load.CustomerId) || string.IsNullOrWhiteSpace(subsidiary))
            return NotScored(name, "Load office/subsidiary or candidate subsidiary is unavailable.");

        // We can't reliably map customer↔subsidiary here, so treat presence of consistent
        // subsidiary data across driver+equipment as a mild positive signal.
        if (!string.IsNullOrWhiteSpace(equipmentSubsidiary)
            && !string.IsNullOrWhiteSpace(driverSubsidiary)
            && string.Equals(equipmentSubsidiary, driverSubsidiary, StringComparison.OrdinalIgnoreCase))
        {
            return new MatchFactor { Name = name, Status = MatchFactorStatus.Strong, Detail = "Driver and equipment share a subsidiary.", Points = _match.FleetAlignmentWeight, MaxPoints = _match.FleetAlignmentWeight };
        }

        return new MatchFactor { Name = name, Status = MatchFactorStatus.Neutral, Detail = "Subsidiary data present but not cross-confirmed.", Points = _match.FleetAlignmentWeight * 0.5, MaxPoints = _match.FleetAlignmentWeight };
    }

    private MatchFactor ScoreEquipmentEvents(EquipmentEventAssessment? events)
    {
        const string name = "Equipment availability";

        // Never assert availability from absent data: when events were not fetched for a known
        // window, this factor is unavailable and excluded from the denominator.
        if (events is not { Evaluated: true })
            return NotScored(name, "Truck/trailer event data was not available for the load window.");

        if (events.HasConflict)
            return new MatchFactor
            {
                Name = name,
                Status = MatchFactorStatus.Weak,
                Detail = $"Equipment event conflict: {string.Join(" ", events.Conflicts)}",
                Points = 0,
                MaxPoints = _match.EquipmentEventsWeight,
            };

        return new MatchFactor
        {
            Name = name,
            Status = MatchFactorStatus.Strong,
            Detail = "No repair/maintenance events overlap the load window.",
            Points = _match.EquipmentEventsWeight,
            MaxPoints = _match.EquipmentEventsWeight,
        };
    }

    private MatchFactor ScoreGeography(LtlLoadSummary load, AlvysDriver? driver)
    {
        const string name = "Origin proximity";
        var originState = load.Origin?.State;
        var driverState = driver?.Address?.State;
        if (string.IsNullOrWhiteSpace(originState) || string.IsNullOrWhiteSpace(driverState))
            return NotScored(name, "Load origin state or driver home state is unavailable.");

        return string.Equals(originState, driverState, StringComparison.OrdinalIgnoreCase)
            ? new MatchFactor { Name = name, Status = MatchFactorStatus.Strong, Detail = $"Driver is based in the origin state ({originState}).", Points = _match.GeographyWeight, MaxPoints = _match.GeographyWeight }
            : new MatchFactor { Name = name, Status = MatchFactorStatus.Neutral, Detail = $"Driver home ({driverState}) differs from origin ({originState}).", Points = _match.GeographyWeight * 0.4, MaxPoints = _match.GeographyWeight };
    }

    private MatchLabel ResolveLabel(int score, bool hasDisqualifier, bool anyFactorAvailable)
    {
        // A hard disqualifier caps the label at Not Recommended regardless of points.
        if (hasDisqualifier) return MatchLabel.NotRecommended;
        if (!anyFactorAvailable) return MatchLabel.NotRecommended;

        if (score >= _match.ExcellentThreshold) return MatchLabel.Excellent;
        if (score >= _match.GoodThreshold) return MatchLabel.Good;
        if (score >= _match.PossibleThreshold) return MatchLabel.Possible;
        if (score >= _match.RiskyThreshold) return MatchLabel.Risky;
        return MatchLabel.NotRecommended;
    }

    private static string BuildSummary(MatchLabel label, List<MatchFactor> factors, List<string> disqualifiers)
    {
        if (disqualifiers.Count > 0)
            return $"{LabelText(label)}: {string.Join(" ", disqualifiers)}";

        var strong = factors.Where(f => f.Status == MatchFactorStatus.Strong).Select(f => f.Name).ToList();
        var weak = factors.Where(f => f.Status == MatchFactorStatus.Weak).Select(f => f.Name).ToList();
        var parts = new List<string>();
        if (strong.Count > 0) parts.Add($"strong on {string.Join(", ", strong)}");
        if (weak.Count > 0) parts.Add($"weak on {string.Join(", ", weak)}");
        var body = parts.Count > 0 ? string.Join("; ", parts) : "scored on available data";
        return $"{LabelText(label)}: {body}.";
    }

    private static MatchFactor NotScored(string name, string detail) =>
        new() { Name = name, Status = MatchFactorStatus.Unavailable, Detail = detail, Points = 0, MaxPoints = 0 };

    private static string LabelText(MatchLabel label) => label switch
    {
        MatchLabel.Excellent => "Excellent Match",
        MatchLabel.Good => "Good Match",
        MatchLabel.Possible => "Possible Match",
        MatchLabel.Risky => "Risky Match",
        _ => "Not Recommended",
    };
}
