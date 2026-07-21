namespace LtlTool.Api.Features.Ltl;

/// <summary>
/// Groups already-normalized <see cref="LtlLoadSummary"/> values by customer, rep, or lane into
/// <see cref="MarginRollupRow"/>s — pure aggregation over data the search/billing-worklist paths
/// already compute, no Alvys calls of its own. Rows are ordered worst-margin-first (nulls last)
/// so a leadership view surfaces risk before it surfaces "no data yet".
/// </summary>
public static class MarginRollupBuilder
{
    public static IReadOnlyList<MarginRollupRow> Build(
        IReadOnlyList<LtlLoadSummary> loads, RollupGroupBy groupBy)
    {
        var grouped = loads
            .Select(l => (Info: DescribeGroup(l, groupBy), Load: l))
            .GroupBy(x => x.Info.Key);

        var rows = grouped
            .Select(g => BuildRow(g.First().Info, [.. g.Select(x => x.Load)]))
            .ToList();

        return rows
            .OrderBy(r => r.TotalGrossMargin is null)
            .ThenBy(r => r.TotalGrossMargin)
            .ToList();
    }

    private readonly record struct GroupInfo(string Key, string Label, bool LabelIsId);

    private static GroupInfo DescribeGroup(LtlLoadSummary load, RollupGroupBy groupBy) => groupBy switch
    {
        RollupGroupBy.Customer => DescribeCustomer(load),
        RollupGroupBy.Rep => DescribeRep(load),
        RollupGroupBy.Lane => DescribeLane(load),
        _ => new GroupInfo("unknown", "Unknown", false),
    };

    private static GroupInfo DescribeCustomer(LtlLoadSummary load)
    {
        if (!string.IsNullOrWhiteSpace(load.CustomerName))
            return new GroupInfo(load.CustomerId ?? load.CustomerName, load.CustomerName, false);
        if (!string.IsNullOrWhiteSpace(load.CustomerId))
            return new GroupInfo(load.CustomerId, load.CustomerId, true);
        return new GroupInfo("unknown", "Unknown customer", false);
    }

    /// <summary>
    /// The Alvys load projection carries only <c>CustomerRepId</c> — no human-readable rep name
    /// field exists anywhere in the read model, so the label says "Rep &lt;id&gt;" and
    /// <see cref="GroupInfo.LabelIsId"/> is set rather than inventing a name.
    /// </summary>
    private static GroupInfo DescribeRep(LtlLoadSummary load) =>
        string.IsNullOrWhiteSpace(load.CustomerRepId)
            ? new GroupInfo("unassigned", "No rep on file", false)
            : new GroupInfo(load.CustomerRepId, $"Rep {load.CustomerRepId}", true);

    /// <summary>
    /// Lane key is the origin/destination city+state label pair. There is no generic lane/corridor
    /// concept in this codebase (the Consolidation Planner's "corridor" is a hardcoded pilot config
    /// for one Laredo↔Dallas warehouse pair, not a general lane key) — this derives one purely from
    /// the normalized origin/destination already on the summary.
    /// </summary>
    private static GroupInfo DescribeLane(LtlLoadSummary load)
    {
        var origin = load.Origin?.Label;
        var destination = load.Destination?.Label;
        if (origin is null && destination is null) return new GroupInfo("unknown", "Unknown lane", false);

        var label = $"{origin ?? "Unknown origin"} → {destination ?? "Unknown destination"}";
        return new GroupInfo(label, label, false);
    }

    private static MarginRollupRow BuildRow(GroupInfo info, IReadOnlyList<LtlLoadSummary> loads)
    {
        var revenues = loads.Where(l => l.Revenue is > 0).Select(l => l.Revenue!.Value).ToList();
        var payables = loads.Where(l => l.CarrierPayable is not null).Select(l => l.CarrierPayable!.Value).ToList();

        // Margin totals only from loads where GrossMargin is already known (both revenue and
        // carrier payable present) — matches the per-load semantics, never mixes a missing side
        // in as zero.
        var marginLoads = loads.Where(l => l.GrossMargin is not null).ToList();
        decimal? totalGrossMargin = marginLoads.Count > 0 ? marginLoads.Sum(l => l.GrossMargin!.Value) : null;
        var marginRevenueBasis = marginLoads.Where(l => l.Revenue is > 0).Sum(l => l.Revenue!.Value);
        decimal? grossMarginPercent = totalGrossMargin is not null && marginRevenueBasis > 0
            ? Math.Round(totalGrossMargin.Value / marginRevenueBasis * 100m, 1)
            : null;

        var unpaidBalances = loads.Where(l => l.Billing.UnpaidBalance is > 0).Select(l => l.Billing.UnpaidBalance!.Value).ToList();

        return new MarginRollupRow
        {
            Key = info.Key,
            Label = info.Label,
            LabelIsId = info.LabelIsId,
            LoadCount = loads.Count,
            TotalRevenue = revenues.Count > 0 ? revenues.Sum() : null,
            TotalCarrierPayable = payables.Count > 0 ? payables.Sum() : null,
            TotalGrossMargin = totalGrossMargin,
            GrossMarginPercent = grossMarginPercent,
            TotalUnpaidBalance = unpaidBalances.Count > 0 ? unpaidBalances.Sum() : null,
            ExceptionCount = loads.Sum(l => l.Exceptions.Count),
            ReadyToBillCount = loads.Count(l => l.Billing.IsReadyToBill),
        };
    }
}
