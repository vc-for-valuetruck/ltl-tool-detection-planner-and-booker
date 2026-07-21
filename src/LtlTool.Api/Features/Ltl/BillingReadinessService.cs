using LtlTool.Api.Features.Integrations.Alvys;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl;

/// <summary>
/// Inspects a load for billing readiness and returns explicit badges, missing fields and
/// risks — never silently defaulting a missing value. A blank rate is "Missing Rate", not
/// <c>$0</c>; an un-reviewed accessorial is flagged; a delivered load with no invoice is a
/// revenue risk.
///
/// POD lives on the separate documents listing, so callers may pass the documents they
/// already fetched. When they do not, POD is reported as <i>not evaluated</i> (the load is
/// not asserted ready, and "Missing POD" is not claimed) rather than guessed either way.
/// </summary>
public sealed class BillingReadinessService(IOptions<LtlOptions> options, TimeProvider clock)
{
    private readonly LtlOptions _options = options.Value;

    /// <summary>Statuses that mean the load has already been invoiced/closed financially.</summary>
    private static readonly HashSet<string> InvoicedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Invoiced", "Financed", "Completed", "Paid",
    };

    /// <summary>Statuses that indicate the freight was (or will be) delivered.</summary>
    private static readonly HashSet<string> DeliveredStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Delivered", "Released", "Released-Carrier Paid", "Carrier Paid",
        "Trip Completed", "Invoiced", "Financed", "Completed", "Paid",
    };

    /// <summary>True when the status indicates the freight was (or will be) delivered.</summary>
    public static bool IsDeliveredStatus(string? status) =>
        !string.IsNullOrWhiteSpace(status) && DeliveredStatuses.Contains(status);

    /// <summary>Statuses that hard-block clean billing (cancelled / no-op freight).</summary>
    private static readonly HashSet<string> BlockingStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cancelled", "TONU",
    };

    /// <summary>
    /// Statuses on an Alvys <see cref="AlvysInvoice"/> that confirm the load has been invoiced.
    /// </summary>
    private static readonly HashSet<string> InvoicePostedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Invoiced", "Sent", "Submitted", "Financed", "Completed", "Paid", "PartiallyPaid", "Partially Paid",
    };

    /// <summary>
    /// Evaluate billing readiness for <paramref name="load"/>. <paramref name="documents"/> is
    /// optional; when supplied it enables POD detection (otherwise POD is not evaluated).
    /// <paramref name="invoices"/> is optional; when supplied (e.g. on the detail path) it
    /// confirms already-invoiced state, surfaces remaining-balance/invoice-status risks and
    /// an aging bucket from the authoritative invoice records rather than inferring from the
    /// load alone. <paramref name="resolvedRevenue"/> should be the caller's already-resolved
    /// revenue figure (<c>LtlNormalizationService.ResolveRevenue</c>) — passed in rather than
    /// recomputed here so the margin risk below always agrees with the revenue the UI displays
    /// (a load with no CustomerRate but a FuelSurcharge/CustomerAccessorials-derived revenue
    /// must not silently skip margin evaluation). <paramref name="carrierPayable"/> is optional
    /// (fetched from the load's trip); when supplied alongside a known revenue it enables a
    /// gross-margin risk signal. <paramref name="accessorialSignals"/> is optional (fetched from
    /// the load's notes/documents on the detail path only); when it carries an evaluated,
    /// typed signal (detention/layover/lumper/reconsignment) and the load has no customer
    /// accessorial charge at all, it flags a likely missed accessorial rather than the narrower
    /// itemization gap <see cref="BillingBadge.MissingAccessorialReview"/> already covers.
    /// <paramref name="carrierAccessorialsTotal"/> is optional (from the same trip fetch as
    /// <paramref name="carrierPayable"/>); when it exceeds the customer's accessorial total it
    /// flags a numeric carrier/customer accessorial mismatch — the carrier was paid for an
    /// accessorial that was never billed through to the customer. When <paramref name="invoices"/>
    /// and <paramref name="resolvedRevenue"/> are both supplied, a posted invoice whose total
    /// drifts from the quoted revenue by more than <see cref="LtlOptions.InvoiceDriftThresholdPercent"/>
    /// is flagged as a possible reclass/reweigh adjustment made after the quote.
    /// </summary>
    public BillingReadinessResult Evaluate(
        AlvysLoad load,
        IReadOnlyList<AlvysLoadDocument>? documents = null,
        IReadOnlyList<AlvysInvoice>? invoices = null,
        decimal? resolvedRevenue = null,
        decimal? carrierPayable = null,
        AccessorialReviewContext? accessorialSignals = null,
        decimal? carrierAccessorialsTotal = null)
    {
        var badges = new List<BillingBadge>();
        var missing = new List<MissingDataFlag>();
        var risks = new List<string>();

        var invoiceConfirmsInvoiced = invoices is { Count: > 0 } && invoices.Any(IsPostedInvoice);

        var alreadyInvoiced =
            load.InvoicedAt is not null
            || (load.InvoicedAmount is > 0)
            || InvoicedStatuses.Contains(load.Status)
            || invoiceConfirmsInvoiced;

        // Invoice-derived revenue-protection signals (only when invoices were supplied).
        decimal? unpaidBalance = null;
        InvoiceAgingBucket? agingBucket = null;
        int? agingDays = null;
        if (invoices is { Count: > 0 })
        {
            var now = clock.GetUtcNow();
            foreach (var invoice in invoices)
            {
                if (invoice.RemainingBalance is > 0)
                {
                    risks.Add(
                        $"Invoice {InvoiceLabel(invoice)} has an unpaid balance of {invoice.RemainingBalance.Value:0.##}.");

                    unpaidBalance = (unpaidBalance ?? 0m) + invoice.RemainingBalance.Value;

                    if (invoice.DueDate is { } dueDate)
                    {
                        var days = (int)(now - dueDate).TotalDays;
                        if (agingDays is null || days > agingDays)
                        {
                            agingDays = days;
                            agingBucket = BucketFor(days);
                        }
                    }
                }
            }
        }

        // Posted-invoice total drifted from the quoted revenue — a proxy for a reclass/reweigh
        // adjustment applied after the quote. Only evaluated against posted invoices with a known
        // total, against the caller's already-resolved revenue; never inferred.
        var invoiceAmountDrift = false;
        if (invoices is { Count: > 0 } && resolvedRevenue is > 0)
        {
            foreach (var invoice in invoices)
            {
                if (!IsPostedInvoice(invoice)) continue;
                var invoiceTotal = invoice.Total?.Amount
                    ?? (invoice.LineItems is { Count: > 0 }
                        ? invoice.LineItems.Sum(li => li.Amount ?? 0m)
                        : (decimal?)null);
                if (invoiceTotal is null) continue;

                var diffPercent = Math.Abs(invoiceTotal.Value - resolvedRevenue.Value) / resolvedRevenue.Value * 100m;
                if ((double)diffPercent >= _options.InvoiceDriftThresholdPercent)
                {
                    invoiceAmountDrift = true;
                    risks.Add(
                        $"Invoice {InvoiceLabel(invoice)} total {invoiceTotal.Value:0.##} differs from the "
                        + $"quoted {resolvedRevenue.Value:0.##} by {diffPercent:0.#}% — possible reclass/reweigh adjustment.");
                }
            }
        }

        var delivered =
            load.ActualDeliveryAt is not null
            || load.DeliveredAt is not null
            || DeliveredStatuses.Contains(load.Status);

        // Rate / revenue.
        var hasRate = load.CustomerRate is > 0 || load.Linehaul is > 0;
        if (!hasRate)
        {
            missing.Add(MissingDataFlag.Rate);
            risks.Add("No customer rate or linehaul on the load.");
        }

        // Gross margin — only when both revenue and carrier payable are known; never inferred.
        // Uses the caller's resolved revenue (not a local recomputation) so this always agrees
        // with LtlLoadSummary.GrossMargin/GrossMarginPercent shown in the UI.
        if (resolvedRevenue is > 0 && carrierPayable is not null)
        {
            var margin = resolvedRevenue.Value - carrierPayable.Value;
            var marginPercent = margin / resolvedRevenue.Value * 100m;
            if (margin < 0)
            {
                risks.Add(
                    $"Negative gross margin: revenue {resolvedRevenue.Value:0.##} vs carrier payable {carrierPayable.Value:0.##}.");
            }
            else if ((double)marginPercent <= _options.MarginRiskThresholdPercent)
            {
                risks.Add($"Thin gross margin: {marginPercent:0.#}% (revenue {resolvedRevenue.Value:0.##}, carrier payable {carrierPayable.Value:0.##}).");
            }
        }

        // Weight.
        if (load.Weight is null or <= 0)
        {
            missing.Add(MissingDataFlag.Weight);
            risks.Add("Shipment weight is missing.");
        }

        // Customer.
        if (string.IsNullOrWhiteSpace(load.CustomerId) && string.IsNullOrWhiteSpace(load.CustomerName))
        {
            missing.Add(MissingDataFlag.Customer);
            risks.Add("No customer is associated with the load.");
        }

        // Accessorials present but not itemized for review.
        var accessorialReviewNeeded =
            load.CustomerAccessorials is > 0
            && (load.CustomerAccessorialsDetails is null || load.CustomerAccessorialsDetails.Count == 0);
        if (accessorialReviewNeeded)
        {
            missing.Add(MissingDataFlag.AccessorialReview);
            risks.Add("Accessorial total present but no itemized detail to review.");
        }

        // Accessorial signal detected in notes/documents (detention/layover/lumper/reconsignment)
        // but no customer accessorial charge exists at all — a likely missed accessorial, distinct
        // from the itemization-gap check above. Only ever evaluated on the detail path, where
        // notes/documents are fetched; absent elsewhere, so this never fires as a false negative.
        var unbilledAccessorialTypes = accessorialSignals is { Evaluated: true }
            ? accessorialSignals.Signals
                .Where(s => s.Type != AccessorialSignalType.Other)
                .Select(s => s.Type)
                .Distinct()
                .ToList()
            : [];
        var possibleUnbilledAccessorial =
            unbilledAccessorialTypes.Count > 0 && load.CustomerAccessorials is null or <= 0;
        if (possibleUnbilledAccessorial)
        {
            risks.Add(
                $"Notes/documents indicate possible {string.Join(", ", unbilledAccessorialTypes)} "
                + "accessorial activity, but the load carries no customer accessorial charge.");
        }

        // Carrier was paid more in accessorials than the customer was billed — a numeric,
        // higher-confidence sibling of the keyword-based check above. A small tolerance absorbs
        // cents-level rounding without masking a real gap.
        var customerAccessorialsTotal = load.CustomerAccessorials is > 0 ? load.CustomerAccessorials.Value : 0m;
        var carrierAccessorialMismatch =
            carrierAccessorialsTotal is > 0
            && carrierAccessorialsTotal.Value > customerAccessorialsTotal + 0.01m;
        if (carrierAccessorialMismatch)
        {
            risks.Add(
                $"Carrier was paid {carrierAccessorialsTotal!.Value:0.##} in accessorials but the "
                + $"customer was billed only {customerAccessorialsTotal:0.##} — a "
                + $"{(carrierAccessorialsTotal.Value - customerAccessorialsTotal):0.##} margin gap.");
        }

        // POD — only when delivered, and only when documents were supplied to look at.
        var podEvaluated = documents is not null;
        var podMissing = false;
        if (delivered && podEvaluated)
        {
            podMissing = !HasProofOfDelivery(documents!);
            if (podMissing)
            {
                missing.Add(MissingDataFlag.Pod);
                risks.Add("Delivered load has no proof-of-delivery document.");
            }
        }

        // Blocking status / deletion.
        var blocked = BlockingStatuses.Contains(load.Status) || load.IsDeleted == true;
        if (blocked)
        {
            risks.Add($"Status '{load.Status}'{(load.IsDeleted == true ? " (deleted)" : string.Empty)} blocks billing.");
        }

        // Assemble badges.
        if (alreadyInvoiced) badges.Add(BillingBadge.AlreadyInvoiced);
        if (!hasRate) badges.Add(BillingBadge.MissingRate);
        if (load.Weight is null or <= 0) badges.Add(BillingBadge.MissingWeight);
        if (podMissing) badges.Add(BillingBadge.MissingPod);
        if (accessorialReviewNeeded) badges.Add(BillingBadge.MissingAccessorialReview);
        if (possibleUnbilledAccessorial) badges.Add(BillingBadge.PossibleUnbilledAccessorial);
        if (carrierAccessorialMismatch) badges.Add(BillingBadge.CarrierAccessorialMismatch);
        if (invoiceAmountDrift) badges.Add(BillingBadge.InvoiceAmountDrift);
        if (missing.Contains(MissingDataFlag.Customer)) badges.Add(BillingBadge.CustomerReviewNeeded);
        if (blocked) badges.Add(BillingBadge.ExceptionBlockingBilling);

        // Ready-to-bill is the conjunction of all clearances and only applies pre-invoice.
        var readyToBill =
            !alreadyInvoiced
            && delivered
            && hasRate
            && load.Weight is > 0
            && !accessorialReviewNeeded
            && !possibleUnbilledAccessorial
            && !carrierAccessorialMismatch
            && !blocked
            && !missing.Contains(MissingDataFlag.Customer)
            && (!podEvaluated || !podMissing);

        if (readyToBill) badges.Add(BillingBadge.ReadyToBill);

        return new BillingReadinessResult
        {
            Badges = badges,
            MissingFields = missing,
            Risks = risks,
            IsReadyToBill = readyToBill,
            IsAlreadyInvoiced = alreadyInvoiced,
            PodEvaluated = podEvaluated,
            UnpaidBalance = unpaidBalance,
            AgingBucket = agingBucket,
            AgingDays = agingDays,
        };
    }

    /// <summary>Buckets days-past-due into the standard Current/30/60/90+ aging convention.</summary>
    private static InvoiceAgingBucket BucketFor(int daysPastDue) => daysPastDue switch
    {
        <= 0 => InvoiceAgingBucket.Current,
        <= 30 => InvoiceAgingBucket.Days1To30,
        <= 60 => InvoiceAgingBucket.Days31To60,
        <= 90 => InvoiceAgingBucket.Days61To90,
        _ => InvoiceAgingBucket.Over90Days,
    };

    /// <summary>True when an invoice record confirms the load was invoiced (submitted/posted status).</summary>
    private static bool IsPostedInvoice(AlvysInvoice invoice) =>
        invoice.IsSubmitted == true
        || invoice.InvoicedDate is not null
        || (invoice.Status is not null && InvoicePostedStatuses.Contains(invoice.Status));

    private static string InvoiceLabel(AlvysInvoice invoice) =>
        !string.IsNullOrWhiteSpace(invoice.Number) ? invoice.Number! : invoice.Id;

    /// <summary>True when any supplied document looks like a proof-of-delivery / signed BOL.</summary>
    private static bool HasProofOfDelivery(IReadOnlyList<AlvysLoadDocument> documents) =>
        documents.Any(d =>
            Contains(d.AttachmentType, "POD")
            || Contains(d.AttachmentType, "Proof of Delivery")
            || Contains(d.AttachmentType, "Signed BOL")
            || Contains(d.AttachmentType, "Delivery Receipt"));

    private static bool Contains(string? value, string token) =>
        value is not null && value.Contains(token, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Operational/revenue-protection exceptions derivable from the load alone. POD-related
    /// exceptions are folded in via <see cref="Evaluate"/> when documents are supplied.
    /// </summary>
    public IReadOnlyList<LtlExceptionFlag> DeriveExceptions(
        AlvysLoad load, BillingReadinessResult billing)
    {
        var exceptions = new List<LtlExceptionFlag>();
        var now = clock.GetUtcNow();

        if (BlockingStatuses.Contains(load.Status) || load.IsDeleted == true)
        {
            exceptions.Add(new LtlExceptionFlag
            {
                Code = "BLOCKING_STATUS",
                Message = $"Load status '{load.Status}'{(load.IsDeleted == true ? " (deleted)" : string.Empty)} blocks normal billing.",
                BlocksBilling = true,
            });
        }

        // Delivered but not invoiced beyond the stale threshold.
        var deliveredAt = load.ActualDeliveryAt ?? load.DeliveredAt;
        if (deliveredAt is not null && !billing.IsAlreadyInvoiced)
        {
            var age = now - deliveredAt.Value;
            if (age.TotalDays >= _options.StaleUninvoicedDays)
            {
                exceptions.Add(new LtlExceptionFlag
                {
                    Code = "STALE_UNINVOICED",
                    Message = $"Delivered {(int)age.TotalDays} days ago and not yet invoiced.",
                    BlocksBilling = false,
                });
            }
        }

        if (billing.Badges.Contains(BillingBadge.MissingRate))
        {
            exceptions.Add(new LtlExceptionFlag
            {
                Code = "MISSING_RATE",
                Message = "No customer rate — revenue at risk.",
                BlocksBilling = true,
            });
        }

        if (billing.Badges.Contains(BillingBadge.MissingPod))
        {
            exceptions.Add(new LtlExceptionFlag
            {
                Code = "MISSING_POD",
                Message = "Delivered without a proof-of-delivery document.",
                BlocksBilling = true,
            });
        }

        if (billing.Badges.Contains(BillingBadge.MissingAccessorialReview))
        {
            exceptions.Add(new LtlExceptionFlag
            {
                Code = "ACCESSORIAL_REVIEW",
                Message = "Accessorial charges need review before billing.",
                BlocksBilling = false,
            });
        }

        if (billing.Badges.Contains(BillingBadge.PossibleUnbilledAccessorial))
        {
            exceptions.Add(new LtlExceptionFlag
            {
                Code = "POSSIBLE_UNBILLED_ACCESSORIAL",
                Message = "Notes/documents suggest an accessorial event occurred, but no accessorial charge is on the load.",
                BlocksBilling = false,
            });
        }

        if (billing.Badges.Contains(BillingBadge.CarrierAccessorialMismatch))
        {
            exceptions.Add(new LtlExceptionFlag
            {
                Code = "CARRIER_ACCESSORIAL_MISMATCH",
                Message = "Carrier accessorial pay exceeds the customer accessorial charge — margin gap.",
                BlocksBilling = false,
            });
        }

        if (billing.Badges.Contains(BillingBadge.InvoiceAmountDrift))
        {
            exceptions.Add(new LtlExceptionFlag
            {
                Code = "INVOICE_AMOUNT_DRIFT",
                Message = "A posted invoice's total differs from the quoted rate — review for a supplemental bill or credit memo.",
                BlocksBilling = false,
            });
        }

        return exceptions;
    }
}
