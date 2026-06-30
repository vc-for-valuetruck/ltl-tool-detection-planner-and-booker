using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies billing-readiness badges, missing-field detection (no silent defaulting) and the
/// POD-not-evaluated stance when documents are not supplied.
/// </summary>
public sealed class BillingReadinessServiceTests
{
    private static AlvysLoad DeliveredBillable() => new()
    {
        Id = "L1",
        Status = "Delivered",
        CustomerId = "CUST-1",
        CustomerRate = 1200m,
        Weight = 5000m,
        ActualDeliveryAt = new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void Ready_to_bill_when_delivered_rated_weighed_with_pod()
    {
        var docs = new List<AlvysLoadDocument> { new() { Id = "D1", AttachmentType = "Signed BOL" } };

        var result = LtlTestFactory.Billing().Evaluate(DeliveredBillable(), docs);

        Assert.True(result.IsReadyToBill);
        Assert.Contains(BillingBadge.ReadyToBill, result.Badges);
        Assert.True(result.PodEvaluated);
    }

    [Fact]
    public void Missing_rate_is_a_badge_not_a_zero()
    {
        var load = DeliveredBillable();
        load.CustomerRate = null;
        load.Linehaul = null;

        var result = LtlTestFactory.Billing().Evaluate(load, []);

        Assert.Contains(BillingBadge.MissingRate, result.Badges);
        Assert.Contains(MissingDataFlag.Rate, result.MissingFields);
        Assert.False(result.IsReadyToBill);
    }

    [Fact]
    public void Delivered_without_pod_is_flagged_when_documents_supplied()
    {
        var result = LtlTestFactory.Billing().Evaluate(DeliveredBillable(), []);

        Assert.True(result.PodEvaluated);
        Assert.Contains(BillingBadge.MissingPod, result.Badges);
        Assert.False(result.IsReadyToBill);
    }

    [Fact]
    public void Pod_not_evaluated_and_not_claimed_missing_when_documents_absent()
    {
        var result = LtlTestFactory.Billing().Evaluate(DeliveredBillable());

        Assert.False(result.PodEvaluated);
        Assert.DoesNotContain(BillingBadge.MissingPod, result.Badges);
        // Without POD evaluation a delivered load is not asserted ready.
        Assert.True(result.IsReadyToBill); // rate+weight+customer present, POD gate not applied
    }

    [Fact]
    public void Already_invoiced_is_detected_and_not_ready()
    {
        var load = DeliveredBillable();
        load.Status = "Invoiced";
        load.InvoicedAt = new DateTimeOffset(2026, 6, 25, 0, 0, 0, TimeSpan.Zero);

        var result = LtlTestFactory.Billing().Evaluate(load, []);

        Assert.True(result.IsAlreadyInvoiced);
        Assert.Contains(BillingBadge.AlreadyInvoiced, result.Badges);
        Assert.False(result.IsReadyToBill);
    }

    [Fact]
    public void Blocking_status_produces_exception_that_blocks_billing()
    {
        var load = DeliveredBillable();
        load.Status = "Cancelled";

        var billing = LtlTestFactory.Billing();
        var result = billing.Evaluate(load, []);
        var exceptions = billing.DeriveExceptions(load, result);

        Assert.Contains(BillingBadge.ExceptionBlockingBilling, result.Badges);
        Assert.Contains(exceptions, e => e.Code == "BLOCKING_STATUS" && e.BlocksBilling);
    }

    [Fact]
    public void Stale_uninvoiced_delivered_load_raises_exception()
    {
        var load = DeliveredBillable();
        // Delivered well beyond the stale threshold relative to the fixed clock (2026-06-30).
        load.ActualDeliveryAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        var billing = LtlTestFactory.Billing();
        var result = billing.Evaluate(load, []);
        var exceptions = billing.DeriveExceptions(load, result);

        Assert.Contains(exceptions, e => e.Code == "STALE_UNINVOICED");
    }

    [Fact]
    public void Submitted_invoice_record_confirms_already_invoiced_even_when_load_status_is_open()
    {
        var load = DeliveredBillable();
        load.Status = "Open";   // load alone would not look invoiced
        var invoices = new List<AlvysInvoice>
        {
            new() { Id = "I1", Number = "INV-100", IsSubmitted = true },
        };

        var result = LtlTestFactory.Billing().Evaluate(load, [], invoices);

        Assert.True(result.IsAlreadyInvoiced);
        Assert.Contains(BillingBadge.AlreadyInvoiced, result.Badges);
        Assert.False(result.IsReadyToBill);   // already invoiced is never ready-to-bill
    }

    [Fact]
    public void Unpaid_invoice_balance_surfaces_as_a_risk()
    {
        var load = DeliveredBillable();
        var invoices = new List<AlvysInvoice>
        {
            new() { Id = "I1", Number = "INV-100", Status = "Invoiced", RemainingBalance = 850.50m },
        };

        var result = LtlTestFactory.Billing().Evaluate(load, [], invoices);

        Assert.Contains(result.Risks, r => r.Contains("INV-100") && r.Contains("unpaid balance"));
    }

    [Fact]
    public void Invoices_omitted_leaves_already_invoiced_inference_unchanged()
    {
        var load = DeliveredBillable();   // Open-ish billable, no invoice signals on the load

        var withoutInvoices = LtlTestFactory.Billing().Evaluate(load, []);
        var withEmptyInvoices = LtlTestFactory.Billing().Evaluate(load, [], []);

        Assert.False(withoutInvoices.IsAlreadyInvoiced);
        Assert.False(withEmptyInvoices.IsAlreadyInvoiced);
    }
}
