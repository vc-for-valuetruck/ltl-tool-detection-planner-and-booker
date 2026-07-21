using LtlTool.Api.Features.Integrations.Alvys;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl;

/// <summary>
/// Normalizes a raw <see cref="AlvysLoad"/> into the decision-support <see cref="LtlLoadSummary"/>:
/// derives origin/destination from stops, classifies LTL freight, computes revenue/RPM, folds in
/// billing readiness + exceptions and — critically — records every value Alvys did not supply as a
/// <see cref="MissingDataFlag"/> rather than coercing it to a default.
/// </summary>
public sealed class LtlNormalizationService(
    IOptions<LtlOptions> options,
    BillingReadinessService billing,
    WorkflowStageService workflow,
    TimeProvider timeProvider)
{
    private readonly LtlOptions _options = options.Value;

    /// <summary>Exception code for a predicted-late arrival (Phase 7.3). Heads-up, not a billing block.</summary>
    public const string PredictedLateExceptionCode = "PredictedLate";

    /// <summary>Exception code for an actual-late delivery (window passed, no arrival recorded). Not a billing block.</summary>
    public const string LateDeliveryExceptionCode = "LateDelivery";

    /// <summary>Statuses that mean capacity is committed (covered → delivered).</summary>
    private static readonly HashSet<string> AssignedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Covered", "Dispatched", "In Transit", "En-Route", "Delivered", "Released",
        "Released-Carrier Paid", "Carrier Paid", "Trip Completed", "Invoiced",
        "Financed", "Completed", "Paid",
    };

    /// <summary>Statuses that mean the load is still an open opportunity.</summary>
    private static readonly HashSet<string> UnassignedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Open", "Quoted", "Reserved", "In Review", "Admin", "Queued",
    };

    /// <summary>
    /// Normalize a load. <paramref name="documents"/> is optional and only used to enable POD
    /// evaluation in the billing-readiness fold-in. <paramref name="invoices"/> is optional and,
    /// when supplied (detail path), confirms already-invoiced state and surfaces invoice-derived
    /// billing risks. <paramref name="visibility"/> and <paramref name="extraExceptions"/> let the
    /// caller fold in visibility-history context and any externally-derived exceptions (e.g. failed
    /// visibility shares) without this service taking an Alvys dependency for the per-load fetch.
    /// <paramref name="carrierPayable"/> is optional (fetched from the load's trip on the detail/
    /// worklist path) and enables a gross-margin risk signal when a revenue figure is also known.
    /// <paramref name="driverTripRate"/> and <paramref name="loadedMiles"/> are the
    /// driver-facing rate (<c>Trip.TripValue.Amount</c>) and loaded miles
    /// (<c>Trip.LoadedMileage.Distance.Value</c>) — the two inputs to the driver-RPM math the
    /// Consolidation Planner needs (Reuben 2026-07-17 sync, 15:55 + 33:06). Both optional; null
    /// on the list/search path where trips are not fetched.
    /// </summary>
    public LtlLoadSummary Normalize(
        AlvysLoad load,
        IReadOnlyList<AlvysLoadDocument>? documents = null,
        IReadOnlyList<AlvysInvoice>? invoices = null,
        VisibilityContext? visibility = null,
        IReadOnlyList<LtlExceptionFlag>? extraExceptions = null,
        decimal? carrierPayable = null,
        decimal? driverTripRate = null,
        decimal? loadedMiles = null,
        LtlEdiEnrichment? ediEnrichment = null,
        LtlLateDelivery? lateDelivery = null)
    {
        var missing = new List<MissingDataFlag>();

        var origin = FirstStopPlace(load, "Pickup");
        var destination = LastStopPlace(load, "Delivery");
        if (origin is null || !origin.HasCityState) missing.Add(MissingDataFlag.Origin);
        if (destination is null || !destination.HasCityState) missing.Add(MissingDataFlag.Destination);

        var scheduledPickup = load.ScheduledPickupAt ?? FirstStopSchedule(load);
        var scheduledDelivery = load.ScheduledDeliveryAt ?? LastStopSchedule(load);
        if (scheduledPickup is null) missing.Add(MissingDataFlag.PickupDate);
        if (scheduledDelivery is null) missing.Add(MissingDataFlag.DeliveryDate);

        var equipment = load.RequiredEquipment is { Count: > 0 }
            ? load.RequiredEquipment.Where(e => !string.IsNullOrWhiteSpace(e)).ToList()
            : [];
        if (equipment.Count == 0) missing.Add(MissingDataFlag.Equipment);

        if (load.Weight is null or <= 0) missing.Add(MissingDataFlag.Weight);
        if (load.CustomerMileage is null or <= 0) missing.Add(MissingDataFlag.Mileage);
        if (string.IsNullOrWhiteSpace(load.CustomerId) && string.IsNullOrWhiteSpace(load.CustomerName))
            missing.Add(MissingDataFlag.Customer);

        // Commodity is not exposed on the Alvys load projection — always surface as missing
        // rather than inventing one.
        missing.Add(MissingDataFlag.Commodity);

        // Per-item freight dimensions (LxWxH / class / stackability) are not on the Alvys load
        // projection either — only aggregate weight/volume/pallets. A 3D trailer-fit verdict is
        // therefore never computable today, so we always flag dimensions as missing rather than
        // implying a fit was checked. Revisit if Alvys read coverage adds item-level dims.
        missing.Add(MissingDataFlag.Dimensions);

        var revenue = ResolveRevenue(load);
        if (revenue is null) missing.Add(MissingDataFlag.Rate);

        var mileage = load.CustomerMileage is > 0 ? load.CustomerMileage : null;
        var revenuePerMile = revenue is > 0 && mileage is > 0
            ? Math.Round(revenue.Value / mileage.Value, 2)
            : (decimal?)null;

        var (isLtl, classification) = ClassifyLtl(load, equipment);

        var billingResult = billing.Evaluate(load, documents, invoices, revenue, carrierPayable);
        var grossMargin = revenue is not null && carrierPayable is not null
            ? revenue.Value - carrierPayable.Value
            : (decimal?)null;
        var grossMarginPercent = grossMargin is not null && revenue is > 0
            ? Math.Round(grossMargin.Value / revenue.Value * 100m, 1)
            : (decimal?)null;
        if (!billingResult.PodEvaluated || billingResult.IsAlreadyInvoiced)
        {
            // InvoiceStatus is known; nothing to flag. (Kept explicit for readability.)
        }
        if (string.IsNullOrWhiteSpace(load.Status)) missing.Add(MissingDataFlag.InvoiceStatus);

        var exceptions = billing.DeriveExceptions(load, billingResult);
        if (extraExceptions is { Count: > 0 })
            exceptions = [.. exceptions, .. extraExceptions];

        var assignment = DeriveAssignment(load.Status);
        var status = string.IsNullOrWhiteSpace(load.Status) ? "Unknown" : load.Status;
        var actualPickup = load.ActualPickupAt ?? load.PickedUpAt;
        var actualDelivery = load.ActualDeliveryAt ?? load.DeliveredAt;
        var visibilityContext = visibility ?? VisibilityContext.NotEvaluated;
        var delivered = actualDelivery is not null || BillingReadinessService.IsDeliveredStatus(load.Status);

        // Phase 7.3: predicted delivery ETA for in-transit loads. A predicted-late arrival is
        // surfaced as a non-billing-blocking exception so it shows on the Exceptions worklist
        // BEFORE an actual-late event, and feeds the T8 notification trigger.
        var eta = EtaEstimator.Estimate(
            timeProvider.GetUtcNow(), actualPickup, actualDelivery, scheduledDelivery, delivered,
            loadedMiles, mileage, _options.Eta);
        if (eta.PredictedLate && eta.PredictedDeliveryAt is not null)
        {
            exceptions =
            [
                .. exceptions,
                new LtlExceptionFlag
                {
                    Code = PredictedLateExceptionCode,
                    Message =
                        $"Predicted late: ETA {eta.PredictedDeliveryAt:g} is past the "
                        + $"{scheduledDelivery:g} delivery window. {eta.Basis}",
                    BlocksBilling = false,
                },
            ];
        }

        // Actual-late delivery (trip-stop derived, passed in by the caller — the load projection
        // carries no stop arrival status). A past fact, not a projection; surfaced as a
        // non-billing-blocking exception and fed to the T8 notification trigger.
        if (lateDelivery is not null)
        {
            exceptions =
            [
                .. exceptions,
                new LtlExceptionFlag
                {
                    Code = LateDeliveryExceptionCode,
                    Message = lateDelivery.Message,
                    BlocksBilling = false,
                },
            ];
        }

        var workflowState = workflow.Evaluate(
            assignment, status, billingResult, exceptions, missing, visibilityContext,
            delivered, hasRevenue: revenue is not null);

        return new LtlLoadSummary
        {
            Id = load.Id,
            LoadNumber = load.LoadNumber,
            OrderNumber = load.OrderNumber,
            PoNumber = load.PONumber,
            CustomerId = load.CustomerId,
            CustomerName = load.CustomerName,
            Status = status,
            Assignment = assignment,
            Origin = origin,
            Destination = destination,
            ScheduledPickupAt = scheduledPickup,
            ScheduledDeliveryAt = scheduledDelivery,
            ActualPickupAt = actualPickup,
            ActualDeliveryAt = actualDelivery,
            PredictedDeliveryAt = eta.PredictedDeliveryAt,
            PredictedLate = eta.PredictedLate,
            EtaBasis = eta.Basis,
            LateDelivery = lateDelivery,
            Equipment = equipment,
            WeightLbs = load.Weight is > 0 ? load.Weight : null,
            Volume = load.Volume is > 0 ? load.Volume : null,
            EdiEnrichment = ediEnrichment,
            Revenue = revenue,
            Mileage = mileage,
            RevenuePerMile = revenuePerMile,
            CarrierPayable = carrierPayable,
            DriverTripRate = driverTripRate,
            LoadedMiles = loadedMiles,
            GrossMargin = grossMargin,
            GrossMarginPercent = grossMarginPercent,
            IsLtl = isLtl,
            LtlClassification = classification,
            MissingData = missing,
            Billing = billingResult,
            Exceptions = exceptions,
            Visibility = visibilityContext,
            Workflow = workflowState,
        };
    }

    /// <summary>
    /// Revenue resolution that does not invent a value: prefer the explicit customer rate, else
    /// sum the rate components (linehaul + fuel + accessorials) when at least one is present,
    /// else null.
    /// </summary>
    private static decimal? ResolveRevenue(AlvysLoad load)
    {
        if (load.CustomerRate is > 0) return load.CustomerRate;

        var components = new[] { load.Linehaul, load.FuelSurcharge, load.CustomerAccessorials };
        if (components.Any(c => c is not null))
        {
            var sum = components.Where(c => c is not null).Sum(c => c!.Value);
            return sum > 0 ? sum : null;
        }

        return null;
    }

    private (bool? IsLtl, string? Classification) ClassifyLtl(AlvysLoad load, List<string> equipment)
    {
        if (!string.IsNullOrWhiteSpace(load.LoadType))
        {
            foreach (var token in _options.LtlLoadTypes)
            {
                if (load.LoadType.Contains(token, StringComparison.OrdinalIgnoreCase))
                    return (true, $"Load type '{load.LoadType}' matches '{token}'.");
            }

            // A definite, non-LTL load type is a real signal too.
            return (false, $"Load type '{load.LoadType}' is not LTL/partial/volume.");
        }

        foreach (var eq in equipment)
        {
            foreach (var hint in _options.LtlEquipmentHints)
            {
                if (eq.Contains(hint, StringComparison.OrdinalIgnoreCase))
                    return (true, $"Equipment '{eq}' hints LTL/partial.");
            }
        }

        // No load type and no equipment hint — do not guess.
        return (null, "No load-type or equipment signal to classify LTL.");
    }

    private static AssignmentState DeriveAssignment(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return AssignmentState.Unknown;
        if (AssignedStatuses.Contains(status)) return AssignmentState.Assigned;
        if (UnassignedStatuses.Contains(status)) return AssignmentState.Unassigned;
        return AssignmentState.Unknown;
    }

    private static LtlPlace? FirstStopPlace(AlvysLoad load, string preferredType) =>
        OrderedStops(load)
            .Where(s => MatchesType(s, preferredType))
            .Select(ToPlace)
            .FirstOrDefault()
        ?? OrderedStops(load).Select(ToPlace).FirstOrDefault();

    private static LtlPlace? LastStopPlace(AlvysLoad load, string preferredType) =>
        OrderedStops(load)
            .Where(s => MatchesType(s, preferredType))
            .Select(ToPlace)
            .LastOrDefault()
        ?? OrderedStops(load).Select(ToPlace).LastOrDefault();

    private static DateTimeOffset? FirstStopSchedule(AlvysLoad load) =>
        OrderedStops(load).Select(s => s.ScheduledStart).FirstOrDefault(d => d is not null);

    private static DateTimeOffset? LastStopSchedule(AlvysLoad load) =>
        OrderedStops(load).Select(s => s.ScheduledEnd ?? s.ScheduledStart).LastOrDefault(d => d is not null);

    private static IEnumerable<AlvysLoadStop> OrderedStops(AlvysLoad load) =>
        (load.Stops ?? []).OrderBy(s => s.Sequence ?? int.MaxValue);

    private static bool MatchesType(AlvysLoadStop stop, string type) =>
        stop.StopType is not null && stop.StopType.Contains(type, StringComparison.OrdinalIgnoreCase);

    private static LtlPlace ToPlace(AlvysLoadStop stop) => new()
    {
        Name = stop.Name,
        City = stop.Address?.City,
        State = stop.Address?.State,
        Zip = stop.Address?.Zip,
    };
}
