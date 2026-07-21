using LtlTool.Api.Features.Ltl;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the pure grouping/aggregation logic behind the margin rollup: customer/rep/lane
/// grouping keys, honest "id-only" labeling when no human-readable name exists, and totals that
/// stay null (never $0) when no load in a group carries the underlying value.
/// </summary>
public sealed class MarginRollupBuilderTests
{
    private static LtlLoadSummary Load(
        string id,
        string? customerId = null,
        string? customerName = null,
        string? customerRepId = null,
        LtlPlace? origin = null,
        LtlPlace? destination = null,
        decimal? revenue = null,
        decimal? carrierPayable = null,
        decimal? grossMargin = null,
        decimal? unpaidBalance = null,
        int exceptionCount = 0,
        bool readyToBill = false) => new()
    {
        Id = id,
        Status = "Delivered",
        CustomerId = customerId,
        CustomerName = customerName,
        CustomerRepId = customerRepId,
        Origin = origin,
        Destination = destination,
        Revenue = revenue,
        CarrierPayable = carrierPayable,
        GrossMargin = grossMargin,
        Exceptions = [.. Enumerable.Range(0, exceptionCount).Select(i => new LtlExceptionFlag { Code = $"E{i}", Message = "x" })],
        Billing = new BillingReadinessResult { UnpaidBalance = unpaidBalance, IsReadyToBill = readyToBill },
    };

    [Fact]
    public void Groups_by_customer_name_and_sums_revenue()
    {
        var loads = new[]
        {
            Load("L1", customerId: "C1", customerName: "Acme", revenue: 500m),
            Load("L2", customerId: "C1", customerName: "Acme", revenue: 300m),
        };

        var rows = MarginRollupBuilder.Build(loads, RollupGroupBy.Customer);

        var row = Assert.Single(rows);
        Assert.Equal("Acme", row.Label);
        Assert.False(row.LabelIsId);
        Assert.Equal(2, row.LoadCount);
        Assert.Equal(800m, row.TotalRevenue);
    }

    [Fact]
    public void Customer_with_no_name_is_labeled_honestly_as_an_id()
    {
        var loads = new[] { Load("L1", customerId: "C1", customerName: null) };

        var row = Assert.Single(MarginRollupBuilder.Build(loads, RollupGroupBy.Customer));

        Assert.Equal("C1", row.Label);
        Assert.True(row.LabelIsId);
    }

    [Fact]
    public void Rep_grouping_labels_the_opaque_id_and_flags_it_as_not_a_name()
    {
        var loads = new[] { Load("L1", customerRepId: "REP-42") };

        var row = Assert.Single(MarginRollupBuilder.Build(loads, RollupGroupBy.Rep));

        Assert.Equal("Rep REP-42", row.Label);
        Assert.True(row.LabelIsId);
    }

    [Fact]
    public void Rep_grouping_buckets_loads_with_no_rep_under_no_rep_on_file()
    {
        var loads = new[] { Load("L1", customerRepId: null) };

        var row = Assert.Single(MarginRollupBuilder.Build(loads, RollupGroupBy.Rep));

        Assert.Equal("No rep on file", row.Label);
        Assert.False(row.LabelIsId);
    }

    [Fact]
    public void Lane_grouping_derives_the_key_from_origin_and_destination_labels()
    {
        var loads = new[]
        {
            Load("L1",
                origin: new LtlPlace { City = "Dallas", State = "TX" },
                destination: new LtlPlace { City = "Atlanta", State = "GA" }),
        };

        var row = Assert.Single(MarginRollupBuilder.Build(loads, RollupGroupBy.Lane));

        Assert.Equal("Dallas, TX → Atlanta, GA", row.Label);
    }

    [Fact]
    public void Lane_grouping_falls_back_to_unknown_lane_when_neither_endpoint_is_known()
    {
        var loads = new[] { Load("L1") };

        var row = Assert.Single(MarginRollupBuilder.Build(loads, RollupGroupBy.Lane));

        Assert.Equal("Unknown lane", row.Label);
    }

    [Fact]
    public void Total_gross_margin_only_sums_loads_where_margin_is_already_known()
    {
        var loads = new[]
        {
            Load("L1", customerName: "Acme", revenue: 1000m, carrierPayable: 700m, grossMargin: 300m),
            // No carrier payable known -> GrossMargin is null on this load; must not count as $0.
            Load("L2", customerName: "Acme", revenue: 500m, grossMargin: null),
        };

        var row = Assert.Single(MarginRollupBuilder.Build(loads, RollupGroupBy.Customer));

        Assert.Equal(300m, row.TotalGrossMargin);
        Assert.Equal(30m, row.GrossMarginPercent); // 300 / 1000 (only the margin-known load's revenue)
    }

    [Fact]
    public void Group_with_no_known_revenue_or_margin_reports_null_not_zero()
    {
        var loads = new[] { Load("L1", customerName: "Acme") };

        var row = Assert.Single(MarginRollupBuilder.Build(loads, RollupGroupBy.Customer));

        Assert.Null(row.TotalRevenue);
        Assert.Null(row.TotalCarrierPayable);
        Assert.Null(row.TotalGrossMargin);
        Assert.Null(row.GrossMarginPercent);
        Assert.Null(row.TotalUnpaidBalance);
    }

    [Fact]
    public void Unpaid_balance_and_exception_and_ready_to_bill_counts_sum_across_the_group()
    {
        var loads = new[]
        {
            Load("L1", customerName: "Acme", unpaidBalance: 200m, exceptionCount: 1, readyToBill: true),
            Load("L2", customerName: "Acme", unpaidBalance: 50m, exceptionCount: 2, readyToBill: false),
        };

        var row = Assert.Single(MarginRollupBuilder.Build(loads, RollupGroupBy.Customer));

        Assert.Equal(250m, row.TotalUnpaidBalance);
        Assert.Equal(3, row.ExceptionCount);
        Assert.Equal(1, row.ReadyToBillCount);
    }

    [Fact]
    public void Rows_are_ordered_worst_margin_first_with_unknown_margin_last()
    {
        var loads = new[]
        {
            Load("L1", customerName: "Healthy", revenue: 1000m, carrierPayable: 500m, grossMargin: 500m),
            Load("L2", customerName: "Thin", revenue: 1000m, carrierPayable: 950m, grossMargin: 50m),
            Load("L3", customerName: "Losing", revenue: 1000m, carrierPayable: 1200m, grossMargin: -200m),
            Load("L4", customerName: "Unknown"), // no margin known
        };

        var rows = MarginRollupBuilder.Build(loads, RollupGroupBy.Customer);

        Assert.Equal(new[] { "Losing", "Thin", "Healthy", "Unknown" }, rows.Select(r => r.Label));
    }
}
