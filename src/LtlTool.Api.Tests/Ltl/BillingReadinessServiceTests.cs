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

    [Fact]
    public void Negative_gross_margin_is_flagged_regardless_of_threshold()
    {
        var load = DeliveredBillable(); // CustomerRate = 1200m
        var result = LtlTestFactory.Billing().Evaluate(load, [], null, resolvedRevenue: 1200m, carrierPayable: 1500m);

        Assert.Contains(result.Risks, r => r.Contains("Negative gross margin"));
    }

    [Fact]
    public void Thin_gross_margin_below_threshold_is_flagged()
    {
        var load = DeliveredBillable(); // CustomerRate = 1200m
        // Margin = 1200 - 1100 = 100 -> 8.33%, below the default 10% threshold.
        var result = LtlTestFactory.Billing().Evaluate(load, [], null, resolvedRevenue: 1200m, carrierPayable: 1100m);

        Assert.Contains(result.Risks, r => r.Contains("Thin gross margin"));
    }

    [Fact]
    public void Healthy_gross_margin_is_not_flagged()
    {
        var load = DeliveredBillable(); // CustomerRate = 1200m
        // Margin = 1200 - 800 = 400 -> 33.3%, comfortably above the default threshold.
        var result = LtlTestFactory.Billing().Evaluate(load, [], null, resolvedRevenue: 1200m, carrierPayable: 800m);

        Assert.DoesNotContain(result.Risks, r => r.Contains("margin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Margin_is_not_evaluated_when_carrier_payable_is_unknown()
    {
        var load = DeliveredBillable();
        var result = LtlTestFactory.Billing().Evaluate(load, [], null, resolvedRevenue: 1200m, carrierPayable: null);

        Assert.DoesNotContain(result.Risks, r => r.Contains("margin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Margin_is_not_evaluated_when_resolved_revenue_is_not_passed_even_with_a_customer_rate()
    {
        var load = DeliveredBillable(); // CustomerRate = 1200m, but caller did not resolve/pass revenue
        var result = LtlTestFactory.Billing().Evaluate(load, [], null, carrierPayable: 1500m);

        Assert.DoesNotContain(result.Risks, r => r.Contains("margin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unpaid_invoice_ages_from_due_date_into_the_correct_bucket()
    {
        var load = DeliveredBillable();
        // Fixed clock is 2026-06-30; due 2026-05-15 is 46 days past due -> Days31To60.
        var invoices = new List<AlvysInvoice>
        {
            new()
            {
                Id = "I1", Number = "INV-200", Status = "Invoiced",
                RemainingBalance = 500m,
                DueDate = new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.Zero),
            },
        };

        var result = LtlTestFactory.Billing().Evaluate(load, [], invoices);

        Assert.Equal(InvoiceAgingBucket.Days31To60, result.AgingBucket);
        Assert.Equal(46, result.AgingDays);
        Assert.Equal(500m, result.UnpaidBalance);
    }

    [Fact]
    public void Multiple_unpaid_invoices_sum_balance_and_use_the_oldest_for_aging()
    {
        var load = DeliveredBillable();
        var invoices = new List<AlvysInvoice>
        {
            new()
            {
                Id = "I1", Number = "INV-1", Status = "Invoiced", RemainingBalance = 200m,
                DueDate = new DateTimeOffset(2026, 6, 25, 0, 0, 0, TimeSpan.Zero), // 5 days -> Days1To30
            },
            new()
            {
                Id = "I2", Number = "INV-2", Status = "Invoiced", RemainingBalance = 300m,
                DueDate = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), // >90 days -> Over90Days
            },
        };

        var result = LtlTestFactory.Billing().Evaluate(load, [], invoices);

        Assert.Equal(500m, result.UnpaidBalance);
        Assert.Equal(InvoiceAgingBucket.Over90Days, result.AgingBucket);
    }

    [Fact]
    public void Fully_paid_invoice_does_not_contribute_to_aging_or_unpaid_balance()
    {
        var load = DeliveredBillable();
        var invoices = new List<AlvysInvoice>
        {
            new()
            {
                Id = "I1", Number = "INV-1", Status = "Paid", RemainingBalance = 0m,
                DueDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            },
        };

        var result = LtlTestFactory.Billing().Evaluate(load, [], invoices);

        Assert.Null(result.UnpaidBalance);
        Assert.Null(result.AgingBucket);
        Assert.Null(result.AgingDays);
    }

    private static AccessorialReviewContext DetentionSignal() => new()
    {
        Evaluated = true,
        Signals =
        [
            new AccessorialSignal
            {
                Type = AccessorialSignalType.Detention,
                EvidenceQuote = "driver waited 3 hours at the dock",
                SourceId = "N1",
                SourceType = "Note",
            },
        ],
    };

    [Fact]
    public void Accessorial_signal_with_no_customer_accessorial_charge_flags_possible_unbilled_accessorial()
    {
        var load = DeliveredBillable();
        load.CustomerAccessorials = null;

        var result = LtlTestFactory.Billing().Evaluate(load, [], accessorialSignals: DetentionSignal());

        Assert.Contains(BillingBadge.PossibleUnbilledAccessorial, result.Badges);
        Assert.Contains(result.Risks, r => r.Contains("Detention") && r.Contains("no customer accessorial charge"));
        Assert.False(result.IsReadyToBill);
    }

    [Fact]
    public void Accessorial_signal_with_an_existing_customer_accessorial_charge_is_not_flagged()
    {
        var load = DeliveredBillable();
        load.CustomerAccessorials = 150m;
        load.CustomerAccessorialsDetails =
            [new AlvysAccessorialDetail { Type = "Detention", Amount = 150m }];

        var result = LtlTestFactory.Billing().Evaluate(load, [], accessorialSignals: DetentionSignal());

        Assert.DoesNotContain(BillingBadge.PossibleUnbilledAccessorial, result.Badges);
    }

    [Fact]
    public void No_accessorial_signal_never_flags_possible_unbilled_accessorial()
    {
        var load = DeliveredBillable();
        load.CustomerAccessorials = null;

        var result = LtlTestFactory.Billing().Evaluate(load, [], accessorialSignals: AccessorialReviewContext.NotEvaluated);

        Assert.DoesNotContain(BillingBadge.PossibleUnbilledAccessorial, result.Badges);
    }

    [Fact]
    public void Accessorial_signals_not_supplied_never_flags_possible_unbilled_accessorial()
    {
        var load = DeliveredBillable();
        load.CustomerAccessorials = null;

        var result = LtlTestFactory.Billing().Evaluate(load, []);

        Assert.DoesNotContain(BillingBadge.PossibleUnbilledAccessorial, result.Badges);
    }

    [Fact]
    public void Possible_unbilled_accessorial_badge_produces_a_non_blocking_exception()
    {
        var load = DeliveredBillable();
        load.CustomerAccessorials = null;

        var billing = LtlTestFactory.Billing();
        var result = billing.Evaluate(load, [], accessorialSignals: DetentionSignal());
        var exceptions = billing.DeriveExceptions(load, result);

        Assert.Contains(exceptions, e => e.Code == "POSSIBLE_UNBILLED_ACCESSORIAL" && !e.BlocksBilling);
    }

    [Fact]
    public void Carrier_accessorial_exceeding_customer_charge_flags_mismatch()
    {
        var load = DeliveredBillable();
        load.CustomerAccessorials = 100m;

        var result = LtlTestFactory.Billing().Evaluate(load, [], carrierAccessorialsTotal: 350m);

        Assert.Contains(BillingBadge.CarrierAccessorialMismatch, result.Badges);
        Assert.Contains(result.Risks, r => r.Contains("350") && r.Contains("100") && r.Contains("margin gap"));
        Assert.False(result.IsReadyToBill);
    }

    [Fact]
    public void Carrier_accessorial_with_no_customer_charge_at_all_flags_mismatch()
    {
        var load = DeliveredBillable();
        load.CustomerAccessorials = null;

        var result = LtlTestFactory.Billing().Evaluate(load, [], carrierAccessorialsTotal: 150m);

        Assert.Contains(BillingBadge.CarrierAccessorialMismatch, result.Badges);
    }

    [Fact]
    public void Carrier_accessorial_at_or_below_customer_charge_is_not_flagged()
    {
        var load = DeliveredBillable();
        load.CustomerAccessorials = 200m;

        var result = LtlTestFactory.Billing().Evaluate(load, [], carrierAccessorialsTotal: 200m);

        Assert.DoesNotContain(BillingBadge.CarrierAccessorialMismatch, result.Badges);
    }

    [Fact]
    public void Carrier_accessorial_total_not_supplied_never_flags_mismatch()
    {
        var load = DeliveredBillable();
        load.CustomerAccessorials = null;

        var result = LtlTestFactory.Billing().Evaluate(load, []);

        Assert.DoesNotContain(BillingBadge.CarrierAccessorialMismatch, result.Badges);
    }

    [Fact]
    public void Carrier_accessorial_mismatch_produces_a_non_blocking_exception()
    {
        var load = DeliveredBillable();
        load.CustomerAccessorials = null;

        var billing = LtlTestFactory.Billing();
        var result = billing.Evaluate(load, [], carrierAccessorialsTotal: 150m);
        var exceptions = billing.DeriveExceptions(load, result);

        Assert.Contains(exceptions, e => e.Code == "CARRIER_ACCESSORIAL_MISMATCH" && !e.BlocksBilling);
    }

    [Fact]
    public void Posted_invoice_total_far_from_quoted_revenue_flags_amount_drift()
    {
        var load = DeliveredBillable(); // CustomerRate = 1200m
        var invoices = new List<AlvysInvoice>
        {
            new()
            {
                Id = "I1", Number = "INV-1", IsSubmitted = true,
                Total = new AlvysMoney { Amount = 1500m },
            },
        };

        var result = LtlTestFactory.Billing().Evaluate(load, [], invoices, resolvedRevenue: 1200m);

        Assert.Contains(BillingBadge.InvoiceAmountDrift, result.Badges);
        Assert.Contains(result.Risks, r => r.Contains("INV-1") && r.Contains("reclass/reweigh"));
    }

    [Fact]
    public void Posted_invoice_total_within_threshold_of_quoted_revenue_is_not_flagged()
    {
        var load = DeliveredBillable(); // CustomerRate = 1200m
        var invoices = new List<AlvysInvoice>
        {
            new() { Id = "I1", Number = "INV-1", IsSubmitted = true, Total = new AlvysMoney { Amount = 1210m } },
        };

        var result = LtlTestFactory.Billing().Evaluate(load, [], invoices, resolvedRevenue: 1200m);

        Assert.DoesNotContain(BillingBadge.InvoiceAmountDrift, result.Badges);
    }

    [Fact]
    public void Non_posted_invoice_drift_is_not_flagged()
    {
        var load = DeliveredBillable(); // CustomerRate = 1200m
        var invoices = new List<AlvysInvoice>
        {
            new() { Id = "I1", Number = "INV-1", Total = new AlvysMoney { Amount = 2000m } }, // not posted
        };

        var result = LtlTestFactory.Billing().Evaluate(load, [], invoices, resolvedRevenue: 1200m);

        Assert.DoesNotContain(BillingBadge.InvoiceAmountDrift, result.Badges);
    }

    [Fact]
    public void Invoice_drift_not_evaluated_without_resolved_revenue()
    {
        var load = DeliveredBillable();
        var invoices = new List<AlvysInvoice>
        {
            new() { Id = "I1", Number = "INV-1", IsSubmitted = true, Total = new AlvysMoney { Amount = 2000m } },
        };

        var result = LtlTestFactory.Billing().Evaluate(load, [], invoices);

        Assert.DoesNotContain(BillingBadge.InvoiceAmountDrift, result.Badges);
    }

    [Fact]
    public void Invoice_amount_drift_falls_back_to_summing_line_items_when_total_is_missing()
    {
        var load = DeliveredBillable(); // CustomerRate = 1200m
        var invoices = new List<AlvysInvoice>
        {
            new()
            {
                Id = "I1", Number = "INV-1", IsSubmitted = true,
                LineItems =
                [
                    new AlvysInvoiceLineItem { Type = "Linehaul", Amount = 1200m },
                    new AlvysInvoiceLineItem { Type = "Accessorial", Amount = 400m },
                ],
            },
        };

        var result = LtlTestFactory.Billing().Evaluate(load, [], invoices, resolvedRevenue: 1200m);

        Assert.Contains(BillingBadge.InvoiceAmountDrift, result.Badges);
    }

    [Fact]
    public void Invoice_amount_drift_produces_a_non_blocking_exception()
    {
        var load = DeliveredBillable();
        var invoices = new List<AlvysInvoice>
        {
            new() { Id = "I1", Number = "INV-1", IsSubmitted = true, Total = new AlvysMoney { Amount = 2000m } },
        };

        var billing = LtlTestFactory.Billing();
        var result = billing.Evaluate(load, [], invoices, resolvedRevenue: 1200m);
        var exceptions = billing.DeriveExceptions(load, result);

        Assert.Contains(exceptions, e => e.Code == "INVOICE_AMOUNT_DRIFT" && !e.BlocksBilling);
    }

    // --- Days-past-terms (Phase 4) --------------------------------------------------------------

    /// <summary>Options with a per-customer net-terms table for the standard test customer.</summary>
    private static LtlOptions WithCustomerTerms(string key, int netDays)
    {
        var options = new LtlOptions();
        options.Billing.CustomerTermsDays[key] = netDays;
        return options;
    }

    [Fact]
    public void Unpaid_invoice_without_due_date_derives_terms_from_customer_config_and_flags_past_terms()
    {
        var load = DeliveredBillable(); // CustomerId = "CUST-1"
        // Invoiced 2026-05-01 + net 15 = due 2026-05-16; clock is 2026-06-30 -> 45 days past.
        var invoices = new List<AlvysInvoice>
        {
            new()
            {
                Id = "I1", Number = "INV-300", Status = "Invoiced", RemainingBalance = 900m,
                InvoicedDate = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            },
        };

        var result = LtlTestFactory.Billing(WithCustomerTerms("CUST-1", 15)).Evaluate(load, [], invoices);

        Assert.Contains(BillingBadge.DaysPastTerms, result.Badges);
        Assert.Equal(45, result.AgingDays);
        Assert.Equal(InvoiceAgingBucket.Days31To60, result.AgingBucket);
        Assert.Contains(result.Risks, r => r.Contains("INV-300") && r.Contains("past terms") && r.Contains("15 day terms"));
    }

    [Fact]
    public void Unpaid_invoice_without_due_date_and_no_configured_terms_leaves_aging_unevaluated()
    {
        var load = DeliveredBillable();
        var invoices = new List<AlvysInvoice>
        {
            new()
            {
                Id = "I1", Number = "INV-301", Status = "Invoiced", RemainingBalance = 900m,
                InvoicedDate = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            },
        };

        // No terms configured -> terms are never invented, so aging cannot be evaluated.
        var result = LtlTestFactory.Billing().Evaluate(load, [], invoices);

        Assert.DoesNotContain(BillingBadge.DaysPastTerms, result.Badges);
        Assert.Null(result.AgingDays);
        Assert.Null(result.AgingBucket);
        Assert.Equal(900m, result.UnpaidBalance); // balance still surfaced
    }

    [Fact]
    public void Unpaid_invoice_with_real_due_date_flags_past_terms_on_the_invoice_due_date_basis()
    {
        var load = DeliveredBillable();
        var invoices = new List<AlvysInvoice>
        {
            new()
            {
                Id = "I1", Number = "INV-302", Status = "Invoiced", RemainingBalance = 400m,
                DueDate = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), // 29 days past
            },
        };

        var result = LtlTestFactory.Billing().Evaluate(load, [], invoices);

        Assert.Contains(BillingBadge.DaysPastTerms, result.Badges);
        Assert.Contains(result.Risks, r => r.Contains("INV-302") && r.Contains("invoice due date"));
    }

    [Fact]
    public void Unpaid_invoice_within_configured_terms_is_not_flagged_past_terms()
    {
        var load = DeliveredBillable();
        // Invoiced 2026-06-20 + net 30 = due 2026-07-20; clock 2026-06-30 -> 20 days remaining.
        var invoices = new List<AlvysInvoice>
        {
            new()
            {
                Id = "I1", Number = "INV-303", Status = "Invoiced", RemainingBalance = 500m,
                InvoicedDate = new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero),
            },
        };

        var result = LtlTestFactory.Billing(WithCustomerTerms("CUST-1", 30)).Evaluate(load, [], invoices);

        Assert.DoesNotContain(BillingBadge.DaysPastTerms, result.Badges);
        Assert.Equal(InvoiceAgingBucket.Current, result.AgingBucket);
    }

    [Fact]
    public void Customer_terms_resolve_by_name_when_id_has_no_entry()
    {
        var load = DeliveredBillable();
        load.CustomerId = null;
        load.CustomerName = "Acme Freight";
        var invoices = new List<AlvysInvoice>
        {
            new()
            {
                Id = "I1", Number = "INV-304", Status = "Invoiced", RemainingBalance = 700m,
                InvoicedDate = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            },
        };

        var result = LtlTestFactory.Billing(WithCustomerTerms("Acme Freight", 15)).Evaluate(load, [], invoices);

        Assert.Contains(BillingBadge.DaysPastTerms, result.Badges);
    }

    [Fact]
    public void Paid_invoice_never_flags_past_terms_even_with_an_old_due_date()
    {
        var load = DeliveredBillable();
        var invoices = new List<AlvysInvoice>
        {
            new()
            {
                Id = "I1", Number = "INV-305", Status = "Paid", RemainingBalance = 0m,
                DueDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            },
        };

        var result = LtlTestFactory.Billing().Evaluate(load, [], invoices);

        Assert.DoesNotContain(BillingBadge.DaysPastTerms, result.Badges);
    }

    // --- Unclassified-document POD honesty (Phase 4) --------------------------------------------

    [Fact]
    public void Delivered_without_pod_notes_unclassified_documents_present()
    {
        var docs = new List<AlvysLoadDocument>
        {
            new() { Id = "D1", AttachmentType = null },
            new() { Id = "D2", AttachmentType = "   " },
        };

        var result = LtlTestFactory.Billing().Evaluate(DeliveredBillable(), docs);

        Assert.Contains(BillingBadge.MissingPod, result.Badges);
        Assert.Contains(result.Risks, r => r.Contains("2 document(s)") && r.Contains("unrecognized type"));
    }

    [Fact]
    public void Delivered_with_recognized_non_pod_document_does_not_note_unclassified_documents()
    {
        // A "Rate Confirmation" is a known, classified type — not a POD, but not ambiguous either.
        var docs = new List<AlvysLoadDocument> { new() { Id = "D1", AttachmentType = "Rate Confirmation" } };

        var result = LtlTestFactory.Billing().Evaluate(DeliveredBillable(), docs);

        Assert.Contains(BillingBadge.MissingPod, result.Badges);
        Assert.DoesNotContain(result.Risks, r => r.Contains("unrecognized type"));
    }

    [Fact]
    public void Delivered_with_no_documents_at_all_does_not_note_unclassified_documents()
    {
        var result = LtlTestFactory.Billing().Evaluate(DeliveredBillable(), []);

        Assert.Contains(BillingBadge.MissingPod, result.Badges);
        Assert.DoesNotContain(result.Risks, r => r.Contains("unrecognized type"));
    }
}
