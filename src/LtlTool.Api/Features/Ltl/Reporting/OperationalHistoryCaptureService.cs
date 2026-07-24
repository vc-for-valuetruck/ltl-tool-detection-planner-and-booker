using LtlTool.Api.Features.Integrations.Alvys;

namespace LtlTool.Api.Features.Ltl.Reporting;

/// <summary>
/// Normalizes accessorial and assignment data out of an already-fetched <see cref="AlvysLoad"/>
/// (+ every matching <see cref="AlvysTrip"/>, not just whichever one a caller separately treats as
/// "the" trip) into the durable history tables (<see cref="AccessorialRecord"/>,
/// <see cref="LoadAssignmentRecord"/>). Deliberately a byproduct of existing reads — this makes no
/// Alvys calls of its own; callers pass in data they already fetched for another purpose (e.g. the
/// load detail view). Every capture is best-effort: a store failure is logged and swallowed so this
/// side channel can never break the read path it's piggybacking on.
///
/// <para>
/// Side-channel only: nothing written here is read back into any live decision-support path
/// (Search, Billing, Assignment, Match). Alvys stays the sole source of truth for current state;
/// these tables exist purely for historical/reporting queries (e.g. Power BI) that Alvys itself has
/// no way to answer, since it reports only the current value with no history of its own.
/// </para>
/// </summary>
public sealed class OperationalHistoryCaptureService(
    IAccessorialStore accessorials,
    ILoadAssignmentStore assignments,
    TimeProvider clock,
    ILogger<OperationalHistoryCaptureService> logger)
{
    /// <summary>
    /// Captures whatever accessorial/assignment data is present on <paramref name="load"/> and
    /// every trip in <paramref name="trips"/> — not just whichever one a caller might separately
    /// treat as "the" economics-bearing trip. A load can have more than one matching trip (e.g.
    /// re-dispatch), and a trip's accessorial/assignment data still matters even when it isn't the
    /// one selected for economics elsewhere. Safe to call with an empty list — customer accessorials
    /// still capture; there's simply nothing trip-side to capture from.
    /// </summary>
    public void Capture(AlvysLoad load, IReadOnlyList<AlvysTrip> trips)
    {
        try
        {
            var now = clock.GetUtcNow();
            CaptureCustomerAccessorials(load, now);
            foreach (var trip in trips)
            {
                CaptureTripAccessorials(load, trip, now);
                CaptureAssignment(load, trip, now);
            }
        }
        catch (Exception ex)
        {
            // Best-effort side channel: never let a capture failure surface to the caller, whose
            // primary job (rendering the load) must succeed regardless of this store's health.
            logger.LogWarning(ex, "Operational history capture failed for load {LoadId}", load.Id);
        }
    }

    private void CaptureCustomerAccessorials(AlvysLoad load, DateTimeOffset now)
    {
        if (load.CustomerAccessorialsDetails is not { Count: > 0 } details) return;

        foreach (var detail in details)
        {
            accessorials.Capture(
                load.Id,
                load.LoadNumber,
                tripId: null,
                new ObservedAccessorialLine(
                    AccessorialEntityType.Customer, detail.Type, detail.Description, detail.Amount),
                now);
        }
    }

    private void CaptureTripAccessorials(AlvysLoad load, AlvysTrip trip, DateTimeOffset now)
    {
        CapturePartyAccessorials(load, trip, AccessorialEntityType.Carrier, trip.Carrier, now);
        CapturePartyAccessorials(load, trip, AccessorialEntityType.Driver1, trip.Driver1, now);
        CapturePartyAccessorials(load, trip, AccessorialEntityType.Driver2, trip.Driver2, now);
        CapturePartyAccessorials(load, trip, AccessorialEntityType.OwnerOperator, trip.OwnerOperator, now);
    }

    private void CapturePartyAccessorials(
        AlvysLoad load, AlvysTrip trip, AccessorialEntityType entityType, AlvysPartyPay? party, DateTimeOffset now)
    {
        if (party?.AccessorialsDetails is not { Count: > 0 } details) return;

        foreach (var detail in details)
        {
            accessorials.Capture(
                load.Id,
                load.LoadNumber,
                trip.Id,
                new ObservedAccessorialLine(entityType, detail.Type, detail.Description, detail.Amount),
                now);
        }
    }

    private void CaptureAssignment(AlvysLoad load, AlvysTrip trip, DateTimeOffset now)
    {
        var snapshot = new ObservedAssignment(
            LoadId: load.Id,
            LoadNumber: load.LoadNumber,
            TripId: trip.Id,
            Status: trip.Status,
            CarrierId: trip.Carrier?.Id,
            CarrierName: trip.Carrier?.Name,
            Driver1Id: trip.Driver1?.Id,
            Driver1Name: trip.Driver1?.Name,
            Driver2Id: trip.Driver2?.Id,
            Driver2Name: trip.Driver2?.Name,
            OwnerOperatorId: trip.OwnerOperator?.Id,
            OwnerOperatorName: trip.OwnerOperator?.Name,
            TruckId: trip.Truck?.Id,
            TrailerId: trip.Trailer?.Id,
            DispatcherId: trip.DispatcherId,
            DispatchedBy: trip.DispatchedBy,
            CarrierAssignedAt: trip.CarrierAssignedAt);

        // Skip an all-empty snapshot (no carrier/driver/truck/trailer/dispatcher at all) — a trip
        // that exists but has nothing assigned yet isn't a reassignment event worth a row.
        if (snapshot is
            {
                CarrierId: null, Driver1Id: null, Driver2Id: null, OwnerOperatorId: null,
                TruckId: null, TrailerId: null, DispatcherId: null,
            })
        {
            return;
        }

        assignments.CaptureIfChanged(snapshot, now);
    }
}
