using LtlTool.Api.Features.Integrations.Alvys;

namespace LtlTool.Api.Features.Ltl;

/// <summary>
/// Deterministic guard against the highest-value LTL payroll leak: a driver paid full loaded
/// mileage on more than one trip of the <em>same consolidation group</em>. When several LTL loads
/// ride one physical linehaul (Phase 5 consolidation), Alvys is supposed to zero the driver-facing
/// <c>Trip.LoadedMileage</c> on the child trips so the driver is paid the miles once, on the main
/// trip. If a child trip keeps non-zero loaded mileage, the same driver gets paid the linehaul
/// twice (or, with two un-zeroed children, "paid triple"). This actually fired in production once.
///
/// <para>
/// The analyzer is pure and read-only. It groups the supplied trips by their <c>Main Load Id</c>
/// trip reference (the parent-load linkage Alvys stamps on consolidation children), and within each
/// group flags any driver that appears on more than one trip carrying non-zero loaded mileage.
/// Every finding cites the real Alvys trip ids, the driver id, and the parent load id it grouped on
/// — nothing is invented. A trip whose loaded mileage is missing or already zero is never a finding;
/// a group with no <c>Main Load Id</c> reference is not a consolidation group and is ignored.
/// </para>
/// </summary>
public sealed class PayrollDoublePayAnalyzer
{
    /// <summary>Trip-reference name that links a consolidation child trip to its parent load.</summary>
    public const string MainLoadIdReference = "Main Load Id";

    /// <summary>Trip-reference name marking a trip as part of an LTL consolidation group.</summary>
    public const string LtlReference = "LTL";

    /// <summary>
    /// Analyze a flat set of trips for same-driver double-pay across consolidation siblings. The
    /// input is expected to be the trips of one or more consolidation groups (e.g. the trips for a
    /// parent load number and its LTL siblings). Returns <see cref="PayrollDoublePayResult.NotEvaluated"/>
    /// when no trip in the set carries a <c>Main Load Id</c> reference — that is honestly "we had
    /// nothing to inspect", never "clean".
    /// </summary>
    public PayrollDoublePayResult Analyze(IReadOnlyList<AlvysTrip> trips)
    {
        if (trips is null || trips.Count == 0) return PayrollDoublePayResult.NotEvaluated;

        // Group by the parent-load linkage. Only trips that carry a non-blank Main Load Id
        // reference are consolidation children we can reason about; everything else is ignored.
        var groups = trips
            .Select(t => (Trip: t, MainLoadId: MainLoadIdOf(t)))
            .Where(x => !string.IsNullOrWhiteSpace(x.MainLoadId))
            .GroupBy(x => x.MainLoadId!, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (groups.Count == 0) return PayrollDoublePayResult.NotEvaluated;

        var findings = new List<PayrollDoublePayFinding>();

        foreach (var group in groups)
        {
            // driver key -> the trips in this group that pay that driver non-zero loaded miles.
            var byDriver = new Dictionary<string, List<PayrollTripPay>>(StringComparer.OrdinalIgnoreCase);
            var driverNames = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            foreach (var (trip, _) in group)
            {
                var miles = trip.LoadedMileage?.Value;
                // A properly-zeroed (or unknown) child trip is not a double-pay: only a trip that
                // actually pays non-zero loaded miles can be a second charge for the same linehaul.
                if (miles is not > 0m) continue;

                foreach (var driver in DriversOf(trip))
                {
                    var key = DriverKey(driver);
                    if (key is null) continue; // Can't attribute pay to an unidentifiable driver.

                    if (!byDriver.TryGetValue(key, out var list))
                    {
                        list = [];
                        byDriver[key] = list;
                        driverNames[key] = driver.Name;
                    }
                    else if (string.IsNullOrWhiteSpace(driverNames[key]) && !string.IsNullOrWhiteSpace(driver.Name))
                    {
                        driverNames[key] = driver.Name;
                    }

                    list.Add(new PayrollTripPay(
                        TripId: string.IsNullOrWhiteSpace(trip.Id) ? null : trip.Id,
                        TripNumber: trip.TripNumber,
                        LoadNumber: trip.LoadNumber,
                        LoadedMiles: miles,
                        TripValue: trip.TripValue?.Amount));
                }
            }

            foreach (var (driverKey, paidTrips) in byDriver)
            {
                // Distinct trips only — the same trip listing a driver in both Driver and Driver1
                // must not be miscounted as two charges.
                var distinctTrips = paidTrips
                    .GroupBy(p => p.TripId ?? p.TripNumber ?? p.LoadNumber ?? Guid.NewGuid().ToString())
                    .Select(g => g.First())
                    .ToList();

                if (distinctTrips.Count < 2) continue;

                var name = driverNames.GetValueOrDefault(driverKey);
                var who = string.IsNullOrWhiteSpace(name) ? $"Driver {driverKey}" : name!;
                findings.Add(new PayrollDoublePayFinding
                {
                    ParentLoadId = group.Key,
                    DriverId = driverKey,
                    DriverName = name,
                    Trips = distinctTrips,
                    Message =
                        $"{who} is paid non-zero loaded miles on {distinctTrips.Count} trips of " +
                        $"consolidation group {group.Key}. Child-trip loaded mileage should be zeroed " +
                        "so the linehaul is paid once — verify before payroll runs.",
                });
            }
        }

        return new PayrollDoublePayResult
        {
            Evaluated = true,
            Findings = findings
                .OrderByDescending(f => f.Trips.Count)
                .ThenBy(f => f.ParentLoadId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            HasDoublePayRisk = findings.Count > 0,
        };
    }

    private static string? MainLoadIdOf(AlvysTrip trip)
    {
        var reference = trip.References?.FirstOrDefault(r =>
            string.Equals(r.Name?.Trim(), MainLoadIdReference, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(reference?.Value) ? null : reference!.Value!.Trim();
    }

    private static IEnumerable<AlvysPartyPay> DriversOf(AlvysTrip trip)
    {
        if (trip.Driver is not null) yield return trip.Driver;
        if (trip.Driver1 is not null) yield return trip.Driver1;
        if (trip.Driver2 is not null) yield return trip.Driver2;
    }

    private static string? DriverKey(AlvysPartyPay driver)
    {
        if (!string.IsNullOrWhiteSpace(driver.Id)) return driver.Id!.Trim();
        if (!string.IsNullOrWhiteSpace(driver.Name)) return driver.Name!.Trim();
        return null;
    }
}
