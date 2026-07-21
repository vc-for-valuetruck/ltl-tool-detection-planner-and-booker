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
        new(client, LtlTestFactory.Normalizer(options), LtlTestFactory.Visibility(),
            LtlTestFactory.AccessorialAnalyzer(), new NullAccessorialSignalExtractor(),
            LtlTestFactory.Options(options), LtlTestFactory.Clock());

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
    public async Task Detail_surfaces_carrier_payable_and_gross_margin_from_trip()
    {
        var client = new FakeAlvysClient
        {
            LoadDetail = new AlvysLoad { Id = "L1", LoadNumber = "100", Status = "Delivered", CustomerRate = 1000m, Weight = 1000m },
            Trips =
            [
                new AlvysTrip
                {
                    Id = "T1", LoadNumber = "100",
                    Carrier = new AlvysPartyPay { Id = "C1", TotalPayable = new AlvysMoney { Amount = 700m, Currency = "USD" } },
                },
            ],
        };

        var summary = await Build(client).GetDetailAsync("100", default);

        Assert.NotNull(summary);
        Assert.Equal(700m, summary!.CarrierPayable);
        Assert.Equal(300m, summary.GrossMargin);
        Assert.Equal(30m, summary.GrossMarginPercent);
    }

    [Fact]
    public async Task Detail_carrier_payable_is_null_when_no_trip_matches_the_load()
    {
        var client = new FakeAlvysClient
        {
            LoadDetail = new AlvysLoad { Id = "L1", LoadNumber = "100", Status = "Delivered", CustomerRate = 1000m, Weight = 1000m },
        };

        var summary = await Build(client).GetDetailAsync("100", default);

        Assert.NotNull(summary);
        Assert.Null(summary!.CarrierPayable);
        Assert.Null(summary.GrossMargin);
    }

    [Fact]
    public async Task BillingWorklist_surfaces_carrier_payable_per_load_from_bulk_trip_fetch()
    {
        var client = new FakeAlvysClient
        {
            Loads =
            [
                new AlvysLoad { Id = "L1", LoadNumber = "100", Status = "Delivered", CustomerRate = 1000m, Weight = 1000m, ActualDeliveryAt = LtlTestFactory.Now },
                new AlvysLoad { Id = "L2", LoadNumber = "200", Status = "Delivered", CustomerRate = 500m, Weight = 500m, ActualDeliveryAt = LtlTestFactory.Now },
            ],
            Trips =
            [
                new AlvysTrip { Id = "T1", LoadNumber = "100", Carrier = new AlvysPartyPay { TotalPayable = new AlvysMoney { Amount = 900m } } },
                // Load 200 has no matching trip — its carrier payable/margin must stay null.
            ],
        };

        var body = await Build(client).BillingWorklistAsync(null, default);

        var l1 = Assert.Single(body, s => s.Id == "L1");
        var l2 = Assert.Single(body, s => s.Id == "L2");
        Assert.Equal(900m, l1.CarrierPayable);
        Assert.Equal(100m, l1.GrossMargin);
        Assert.Null(l2.CarrierPayable);
        Assert.Null(l2.GrossMargin);
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

    private static AlvysLoad LaneLoad(
        string id, string number, string originState, string destinationState,
        decimal rate, decimal mileage) => new()
    {
        Id = id,
        LoadNumber = number,
        Status = "Delivered",
        CustomerRate = rate,
        CustomerMileage = mileage,
        Stops =
        [
            new AlvysLoadStop { Sequence = 1, StopType = "Pickup", Address = new AlvysAddress { City = "A", State = originState } },
            new AlvysLoadStop { Sequence = 2, StopType = "Delivery", Address = new AlvysAddress { City = "B", State = destinationState } },
        ],
    };

    [Fact]
    public async Task LaneRateContext_reports_median_and_range_from_priced_delivered_loads()
    {
        var client = new FakeAlvysClient
        {
            Loads =
            [
                LaneLoad("L1", "100", "TX", "IL", rate: 400m, mileage: 100m), // RPM 4.00
                LaneLoad("L2", "200", "TX", "IL", rate: 500m, mileage: 100m), // RPM 5.00
                LaneLoad("L3", "300", "TX", "IL", rate: 600m, mileage: 100m), // RPM 6.00
                LaneLoad("L4", "400", "CA", "NV", rate: 900m, mileage: 100m), // different lane — excluded
            ],
        };

        var context = await Build(client).LaneRateContextAsync("TX", "IL", default);

        Assert.Equal(3, context.SampleSize);
        Assert.Equal(5.00m, context.MedianRpm);
        Assert.Equal(4.00m, context.MinRpm);
        Assert.Equal(6.00m, context.MaxRpm);
        Assert.Contains("Recent tenant history", context.Basis);
    }

    [Fact]
    public async Task LaneRateContext_is_insufficient_below_the_min_sample_threshold()
    {
        var client = new FakeAlvysClient
        {
            Loads =
            [
                LaneLoad("L1", "100", "TX", "IL", rate: 400m, mileage: 100m),
                LaneLoad("L2", "200", "TX", "IL", rate: 500m, mileage: 100m),
            ],
        };

        var context = await Build(client).LaneRateContextAsync("TX", "IL", default);

        Assert.Equal(2, context.SampleSize);
        Assert.Null(context.MedianRpm);
        Assert.Null(context.MinRpm);
        Assert.Null(context.MaxRpm);
        Assert.Contains("Not enough", context.Basis);
    }

    [Fact]
    public async Task LaneRateContext_ignores_loads_missing_rate_or_mileage()
    {
        var client = new FakeAlvysClient
        {
            Loads =
            [
                LaneLoad("L1", "100", "TX", "IL", rate: 400m, mileage: 100m),
                LaneLoad("L2", "200", "TX", "IL", rate: 500m, mileage: 100m),
                // Missing mileage → no RPM → not counted, so the lane stays below threshold.
                new AlvysLoad
                {
                    Id = "L3", LoadNumber = "300", Status = "Delivered", CustomerRate = 600m,
                    Stops =
                    [
                        new AlvysLoadStop { Sequence = 1, Address = new AlvysAddress { City = "A", State = "TX" } },
                        new AlvysLoadStop { Sequence = 2, Address = new AlvysAddress { City = "B", State = "IL" } },
                    ],
                },
            ],
        };

        var context = await Build(client).LaneRateContextAsync("TX", "IL", default);

        Assert.Equal(2, context.SampleSize);
        Assert.Null(context.MedianRpm);
    }

    [Fact]
    public async Task Exceptions_unions_active_transit_sweep_so_in_transit_late_delivery_surfaces()
    {
        // The general recency-ordered sweep does NOT return this in-transit load (it fell outside
        // the bounded window); only the explicit active-transit sweep does. The union must still
        // surface its trip-stop late-delivery exception — the Item 2 live gap.
        var lateDeliveryStop = new AlvysTripStop
        {
            Id = "d-1004253",
            StopType = "Delivery",
            Sequence = 1,
            Address = new AlvysAddress { City = "Laredo", State = "TX" },
            // Two days before the fixed test clock, no arrival recorded.
            AppointmentDate = LtlTestFactory.Now.AddDays(-2),
        };
        var client = new StatusRoutingAlvysClient
        {
            Loads = [], // general sweep window misses it
            ActiveTransitLoads =
            [
                new AlvysLoad { Id = "ACT", LoadNumber = "1004253", Status = "In Transit", CustomerRate = 500m, Weight = 1000m },
            ],
            Trips =
            [
                new AlvysTrip
                {
                    LoadNumber = "1004253",
                    Stops = [new AlvysTripStop { StopType = "Pickup", Sequence = 0 }, lateDeliveryStop],
                },
            ],
        };

        var body = await Build(client).ExceptionsAsync(default);

        var summary = Assert.Single(body, s => s.Id == "ACT");
        Assert.NotNull(summary.LateDelivery);
        Assert.Contains(summary.Exceptions, e => e.Code == LtlNormalizationService.LateDeliveryExceptionCode);
    }

    /// <summary>
    /// Fake that routes the exception sweep's two passes to different load sets: the bare (general)
    /// sweep returns <see cref="FakeAlvysClient.Loads"/>; the active-transit sweep (whose Status
    /// equals <see cref="LoadSearchRequest.ActiveTransitStatuses"/>) returns
    /// <see cref="ActiveTransitLoads"/>. Lets a test prove the union covers loads the general pass
    /// misses.
    /// </summary>
    private sealed class StatusRoutingAlvysClient : FakeAlvysClient
    {
        public List<AlvysLoad> ActiveTransitLoads { get; set; } = [];

        public override Task<AlvysLoadsResponse> SearchLoadsAsync(
            LoadSearchRequest request, CancellationToken ct = default)
        {
            var isActiveTransit = request.Status is not null
                && request.Status.SequenceEqual(LoadSearchRequest.ActiveTransitStatuses);
            var source = isActiveTransit ? ActiveTransitLoads : Loads;
            var items = source.Skip(request.Page * request.PageSize).Take(request.PageSize).ToList();
            return Task.FromResult(new AlvysLoadsResponse
            {
                Page = request.Page,
                PageSize = request.PageSize,
                Total = source.Count,
                Items = items,
            });
        }
    }
}
