using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the LtlLoadService orchestration of the newly wired read-only context: invoice-backed
/// billing worklist/detail, and bounded visibility-history enrichment of the exception list.
/// </summary>
public sealed class LtlLoadServiceTests
{
    private static LtlLoadService Build(FakeAlvysClient client, LtlOptions? options = null) =>
        new(client, LtlTestFactory.Normalizer(options), LtlTestFactory.Visibility(), LtlTestFactory.Options(options));

    [Fact]
    public async Task Detail_marks_load_already_invoiced_from_invoice_record()
    {
        var client = new FakeAlvysClient
        {
            LoadDetail = new AlvysLoad
            {
                Id = "L1", LoadNumber = "100", Status = "Delivered",
                CustomerRate = 500m, Weight = 1000m, ActualDeliveryAt = LtlTestFactory.Now,
            },
            Invoices =
            [
                new AlvysInvoice
                {
                    Id = "INV1", Number = "INV-100", Status = "Sent", IsSubmitted = true,
                    InvoicedDate = LtlTestFactory.Now,
                    Loads = [new AlvysInvoiceLoadRef { LoadNumber = "100" }],
                },
            ],
        };

        var summary = await Build(client).GetDetailAsync("100", default);

        Assert.NotNull(summary);
        Assert.True(summary!.Billing.IsAlreadyInvoiced);
        Assert.Contains(BillingBadge.AlreadyInvoiced, summary.Billing.Badges);
        Assert.False(summary.Billing.IsReadyToBill);
    }

    [Fact]
    public async Task Detail_surfaces_unpaid_invoice_balance_as_risk()
    {
        var client = new FakeAlvysClient
        {
            LoadDetail = new AlvysLoad { Id = "L1", LoadNumber = "100", Status = "Invoiced", CustomerRate = 500m, Weight = 1000m },
            Invoices =
            [
                new AlvysInvoice
                {
                    Id = "INV1", Number = "INV-100", Status = "PartiallyPaid", RemainingBalance = 250m,
                    Loads = [new AlvysInvoiceLoadRef { LoadNumber = "100" }],
                },
            ],
        };

        var summary = await Build(client).GetDetailAsync("100", default);

        Assert.NotNull(summary);
        Assert.Contains(summary!.Billing.Risks, r => r.Contains("unpaid balance"));
    }

    [Fact]
    public async Task BillingWorklist_excludes_invoice_confirmed_loads_but_keeps_unbilled()
    {
        var client = new FakeAlvysClient
        {
            Loads =
            [
                new AlvysLoad { Id = "L1", LoadNumber = "100", Status = "Delivered", CustomerRate = 100m, Weight = 1000m, ActualDeliveryAt = LtlTestFactory.Now },
                new AlvysLoad { Id = "L2", LoadNumber = "200", Status = "Delivered", CustomerRate = 200m, Weight = 2000m, ActualDeliveryAt = LtlTestFactory.Now },
            ],
            // Only load 200 has a posted invoice; the load status itself is not "Invoiced".
            Invoices =
            [
                new AlvysInvoice { Id = "INV2", Number = "INV-200", IsSubmitted = true, InvoicedDate = LtlTestFactory.Now,
                    Loads = [new AlvysInvoiceLoadRef { LoadNumber = "200" }] },
            ],
        };

        var body = await Build(client).BillingWorklistAsync(null, default);

        Assert.Contains(body, s => s.Id == "L1");
        Assert.DoesNotContain(body, s => s.Id == "L2"); // invoice-confirmed → off the worklist
    }

    [Fact]
    public async Task Exceptions_surfaces_visibility_failures_within_the_enrich_bound()
    {
        var client = new FakeAlvysClient
        {
            Loads = [new AlvysLoad { Id = "L1", LoadNumber = "100", Status = "In Transit", CustomerRate = 500m, Weight = 1000m }],
            InboundVisibility =
            [
                new AlvysVisibilityHistoryEvent { LoadNumber = "100", EventType = "Arrival", Status = "Failed", Error = "401" },
            ],
        };

        var body = await Build(client).ExceptionsAsync(default);

        var summary = Assert.Single(body, s => s.Id == "L1");
        Assert.Contains(summary.Exceptions, e => e.Code == "VISIBILITY_FAILED");
    }

    [Fact]
    public async Task Exceptions_does_not_enrich_beyond_the_configured_bound()
    {
        var options = new LtlOptions { MaxVisibilityEnriched = 0 };
        var client = new FakeAlvysClient
        {
            Loads = [new AlvysLoad { Id = "L1", LoadNumber = "100", Status = "In Transit", CustomerRate = 500m, Weight = 1000m }],
            InboundVisibility =
            [
                new AlvysVisibilityHistoryEvent { LoadNumber = "100", EventType = "Arrival", Status = "Failed", Error = "401" },
            ],
        };

        var body = await Build(client, options).ExceptionsAsync(default);

        // With the enrich bound at 0, the visibility-only failure must not appear here (detail path only).
        Assert.DoesNotContain(body, s => s.Exceptions.Any(e => e.Code == "VISIBILITY_FAILED"));
    }
}
