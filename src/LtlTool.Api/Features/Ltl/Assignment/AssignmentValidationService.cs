using LtlTool.Api.Features.Integrations.Alvys;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl.Assignment;

/// <summary>
/// Validates a proposed <i>internal</i> assignment before it is recorded. This is the guard on the
/// audited assignment boundary: it never writes to Alvys, it only inspects the normalized load and
/// the resolved fleet candidate and returns explainable findings.
///
/// <para>
/// Hard rules (no driver, terminated/inactive driver, expired credentials, weight over trailer
/// capacity) are <see cref="AssignmentIssueSeverity.Block"/> and refuse the assignment. Softer
/// concerns (equipment mismatch, expiring credentials, a pickup window already in the past, missing
/// billing-critical data) are <see cref="AssignmentIssueSeverity.Warn"/>: the dispatcher keeps
/// control and may proceed, but the warning is recorded on the audit alongside any override reason.
/// </para>
/// </summary>
public sealed class AssignmentValidationService(IOptions<LtlOptions> options, TimeProvider clock)
{
    private readonly LtlMatchOptions _match = options.Value.Match;

    /// <summary>
    /// Validate <paramref name="request"/> against the normalized <paramref name="load"/> and the
    /// resolved fleet <paramref name="candidate"/> (driver/truck/trailer looked up by id; any member
    /// may be null when the id was not supplied or did not resolve).
    /// </summary>
    public AssignmentValidationResult Validate(
        LtlLoadSummary load, AssignmentRequest request, MatchCandidate candidate,
        EquipmentEventAssessment? equipmentEvents = null,
        YardPresenceAssessment yardPresence = default)
    {
        var issues = new List<AssignmentIssue>();
        var now = clock.GetUtcNow();

        ValidateDriver(request, candidate.Driver, now, issues);
        ValidateCapacity(load, candidate.Trailer, issues);
        ValidateEquipment(load, candidate.Trailer, issues);
        ValidateEquipmentEvents(equipmentEvents, issues);
        ValidateWindows(load, now, issues);
        ValidateLoadData(load, issues);
        ValidateYardPresence(yardPresence, issues);

        return new AssignmentValidationResult { Issues = issues };
    }

    /// <summary>
    /// Folds the yard-presence signal into validation. Only runs when a lookup was actually attempted
    /// (the Yard integration is configured), so unconfigured deployments never warn. A security hold on
    /// the release is a hard <see cref="AssignmentIssueSeverity.Block"/>; an unreachable yard and
    /// equipment not physically at the yard are non-blocking <see cref="AssignmentIssueSeverity.Warn"/>
    /// findings — presence is a peer signal, never fabricated into a pass.
    /// </summary>
    private static void ValidateYardPresence(
        YardPresenceAssessment assessment, List<AssignmentIssue> issues)
    {
        if (!assessment.Attempted)
            return;

        var presence = assessment.Presence;
        if (presence is null)
        {
            // The yard was consulted but could not be reached / answered — never assume presence.
            issues.Add(Warn("YARD_PRESENCE_UNAVAILABLE",
                "Yard presence could not be confirmed (yard unreachable). Equipment location is unverified."));
            return;
        }

        if (presence.SecurityHold)
        {
            issues.Add(Block("SECURITY_HOLD_ON_RELEASE",
                "The yard has placed a security hold on this equipment's release. Assignment is blocked until cleared."));
        }

        if (!presence.AtYard)
        {
            issues.Add(Warn("EQUIPMENT_NOT_AT_YARD",
                presence.OnRecord
                    ? "Equipment is not currently at the yard per the yard system."
                    : "The yard has no record of this equipment; its yard presence is unverified."));
        }
    }

    private static void ValidateEquipmentEvents(
        EquipmentEventAssessment? events, List<AssignmentIssue> issues)
    {
        // Only warns when events were actually fetched and a conflict was found; absent/unfetched
        // event data never blocks or warns (we do not assert availability from missing data).
        if (events is { Evaluated: true, HasConflict: true })
            issues.Add(Warn("EQUIPMENT_EVENT_CONFLICT",
                $"Equipment has events overlapping the load window: {string.Join(" ", events.Conflicts)}"));
    }

    private void ValidateDriver(
        AssignmentRequest request, AlvysDriver? driver, DateTimeOffset now, List<AssignmentIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(request.DriverId))
        {
            issues.Add(Block("NO_DRIVER", "No driver selected for the assignment."));
            return;
        }

        if (driver is null)
        {
            // An id was supplied but did not resolve in the active fleet — warn rather than block,
            // since the read-only fleet sweep is bounded and may not include every driver.
            issues.Add(Warn("DRIVER_UNRESOLVED",
                "Selected driver could not be confirmed against the active fleet."));
            return;
        }

        if (driver.TerminatedAt is not null && driver.TerminatedAt <= now)
            issues.Add(Block("DRIVER_TERMINATED", "Selected driver is terminated."));
        else if (driver.IsActive == false)
            issues.Add(Block("DRIVER_INACTIVE", "Selected driver is not active."));

        CheckExpiry(driver.LicenseExpiresAt, "License", now, issues);
        CheckExpiry(driver.MedicalExpiresAt, "Medical", now, issues);
    }

    private void CheckExpiry(
        DateTimeOffset? expiry, string label, DateTimeOffset now, List<AssignmentIssue> issues)
    {
        if (expiry is null) return;

        if (expiry <= now)
            issues.Add(Block($"{label.ToUpperInvariant()}_EXPIRED",
                $"Driver {label.ToLowerInvariant()} expired on {expiry:yyyy-MM-dd}."));
        else if ((expiry.Value - now).TotalDays <= _match.CredentialExpiryWarningDays)
            issues.Add(Warn($"{label.ToUpperInvariant()}_EXPIRING",
                $"Driver {label.ToLowerInvariant()} expires {expiry:yyyy-MM-dd}."));
    }

    private static void ValidateCapacity(
        LtlLoadSummary load, AlvysTrailerEquipment? trailer, List<AssignmentIssue> issues)
    {
        var capacity = trailer?.Capacity?.Weight;
        if (load.WeightLbs is not > 0 || capacity is not > 0) return;

        if (load.WeightLbs.Value > capacity.Value)
            issues.Add(Block("OVER_CAPACITY",
                $"Load weight {load.WeightLbs.Value:n0} lbs exceeds trailer capacity {capacity.Value:n0} lbs."));
    }

    private static void ValidateEquipment(
        LtlLoadSummary load, AlvysTrailerEquipment? trailer, List<AssignmentIssue> issues)
    {
        var trailerType = trailer?.EquipmentType;
        if (load.Equipment.Count == 0 || string.IsNullOrWhiteSpace(trailerType)) return;

        var compatible = load.Equipment.Any(e =>
            string.Equals(e, trailerType, StringComparison.OrdinalIgnoreCase)
            || e.Contains(trailerType, StringComparison.OrdinalIgnoreCase)
            || trailerType.Contains(e, StringComparison.OrdinalIgnoreCase));

        if (!compatible)
            issues.Add(Warn("EQUIPMENT_MISMATCH",
                $"Trailer {trailerType} does not match required {string.Join("/", load.Equipment)}."));
    }

    private static void ValidateWindows(LtlLoadSummary load, DateTimeOffset now, List<AssignmentIssue> issues)
    {
        if (load.ScheduledPickupAt is { } pickup && pickup < now && load.ActualPickupAt is null)
            issues.Add(Warn("PICKUP_WINDOW_PASSED",
                $"Scheduled pickup ({pickup:yyyy-MM-dd}) is in the past and the load is not picked up."));
    }

    private static void ValidateLoadData(LtlLoadSummary load, List<AssignmentIssue> issues)
    {
        if (load.MissingData.Contains(MissingDataFlag.Origin)
            || load.MissingData.Contains(MissingDataFlag.Destination))
            issues.Add(Warn("MISSING_LANE", "Load origin or destination is incomplete."));

        if (load.MissingData.Contains(MissingDataFlag.Rate))
            issues.Add(Warn("MISSING_RATE", "No customer rate on the load — billing revenue is at risk."));

        if (load.MissingData.Contains(MissingDataFlag.Weight))
            issues.Add(Warn("MISSING_WEIGHT", "Shipment weight is missing — capacity cannot be confirmed."));
    }

    private static AssignmentIssue Block(string code, string message) =>
        new() { Code = code, Message = message, Severity = AssignmentIssueSeverity.Block };

    private static AssignmentIssue Warn(string code, string message) =>
        new() { Code = code, Message = message, Severity = AssignmentIssueSeverity.Warn };
}
