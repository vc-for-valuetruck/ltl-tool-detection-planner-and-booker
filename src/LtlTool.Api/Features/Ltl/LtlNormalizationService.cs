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
    IOptions<LtlOptions> options, BillingReadinessService billing, WorkflowStageService workflow)
{
    private readonly LtlOptions _options = options.Value;

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
    /// </summary>
    public LtlLoadSummary Normalize(
        AlvysLoad load,
        IReadOnlyList<AlvysLoadDocument>? documents = null,
        IReadOnlyList<AlvysInvoice>? invoices = null,
        VisibilityContext? visibility = null,
        IReadOnlyList<LtlExceptionFlag>? extraExceptions = null)
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

        var revenue = ResolveRevenue(load);
        if (revenue is null) missing.Add(MissingDataFlag.Rate);

        var mileage = load.CustomerMileage is > 0 ? load.CustomerMileage : null;
        var revenuePerMile = revenue is > 0 && mileage is > 0
            ? Math.Round(revenue.Value / mileage.Value, 2)
            : (decimal?)null;

        var (isLtl, classification) = ClassifyLtl(load, equipment);

        var billingResult = billing.Evaluate(load, documents, invoices);
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
        var actualDelivery = load.ActualDeliveryAt ?? load.DeliveredAt;
        var visibilityContext = visibility ?? VisibilityContext.NotEvaluated;
        var delivered = actualDelivery is not null || BillingReadinessService.IsDeliveredStatus(load.Status);

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
            ActualPickupAt = load.ActualPickupAt ?? load.PickedUpAt,
            ActualDeliveryAt = actualDelivery,
            Equipment = equipment,
            WeightLbs = load.Weight is > 0 ? load.Weight : null,
            Volume = load.Volume is > 0 ? load.Volume : null,
            Revenue = revenue,
            Mileage = mileage,
            RevenuePerMile = revenuePerMile,
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
