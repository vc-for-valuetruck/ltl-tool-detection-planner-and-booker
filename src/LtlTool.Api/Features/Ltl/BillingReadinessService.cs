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

    /// <summary>Statuses that hard-block clean billing (cancelled / no-op freight).</summary>
    private static readonly HashSet<string> BlockingStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cancelled", "TONU",
    };

    /// <summary>
    /// Evaluate billing readiness for <paramref name="load"/>. <paramref name="documents"/> is
    /// optional; when supplied it enables POD detection (otherwise POD is not evaluated).
    /// </summary>
    public BillingReadinessResult Evaluate(
        AlvysLoad load, IReadOnlyList<AlvysLoadDocument>? documents = null)
    {
        var badges = new List<BillingBadge>();
        var missing = new List<MissingDataFlag>();
        var risks = new List<string>();

        var alreadyInvoiced =
            load.InvoicedAt is not null
            || (load.InvoicedAmount is > 0)
            || InvoicedStatuses.Contains(load.Status);

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
        if (missing.Contains(MissingDataFlag.Customer)) badges.Add(BillingBadge.CustomerReviewNeeded);
        if (blocked) badges.Add(BillingBadge.ExceptionBlockingBilling);

        // Ready-to-bill is the conjunction of all clearances and only applies pre-invoice.
        var readyToBill =
            !alreadyInvoiced
            && delivered
            && hasRate
            && load.Weight is > 0
            && !accessorialReviewNeeded
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
        };
    }

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

        return exceptions;
    }
}
