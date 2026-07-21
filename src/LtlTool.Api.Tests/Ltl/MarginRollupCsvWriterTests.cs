using LtlTool.Api.Features.Ltl;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Verifies the CSV rendering of the margin rollup: RFC 4180 quoting for commas/quotes in labels,
/// missing totals written as empty cells (never "0"), and the truncated flag surfaced per row so a
/// BI report built on this feed never mistakes a bounded scan for a complete one.
/// </summary>
public sealed class MarginRollupCsvWriterTests
{
    private static MarginRollupRow Row(
        string key = "k",
        string label = "Label",
        bool labelIsId = false,
        int loadCount = 1,
        decimal? totalRevenue = null,
        decimal? totalGrossMargin = null,
        decimal? grossMarginPercent = null) => new()
    {
        Key = key,
        Label = label,
        LabelIsId = labelIsId,
        LoadCount = loadCount,
        TotalRevenue = totalRevenue,
        TotalGrossMargin = totalGrossMargin,
        GrossMarginPercent = grossMarginPercent,
    };

    [Fact]
    public void Writes_the_header_row_first()
    {
        var csv = MarginRollupCsvWriter.Write(new MarginRollupResponse { GroupBy = RollupGroupBy.Customer, Rows = [] });

        var firstLine = csv.Split("\r\n")[0];
        Assert.Equal(
            "GroupBy,Key,Label,LabelIsId,LoadCount,TotalRevenue,TotalCarrierPayable,TotalGrossMargin," +
            "GrossMarginPercent,TotalUnpaidBalance,ExceptionCount,ReadyToBillCount,Truncated",
            firstLine);
    }

    [Fact]
    public void Empty_rows_produce_only_the_header()
    {
        var csv = MarginRollupCsvWriter.Write(new MarginRollupResponse { GroupBy = RollupGroupBy.Customer, Rows = [] });

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
    }

    [Fact]
    public void Missing_totals_render_as_empty_cells_not_zero()
    {
        var csv = MarginRollupCsvWriter.Write(new MarginRollupResponse
        {
            GroupBy = RollupGroupBy.Customer,
            Rows = [Row(totalRevenue: null, totalGrossMargin: null, grossMarginPercent: null)],
        });

        var dataLine = csv.Split("\r\n")[1];
        // TotalRevenue, TotalCarrierPayable, TotalGrossMargin, GrossMarginPercent, TotalUnpaidBalance
        // are columns 6-10 (1-indexed) — all empty for this row.
        var fields = dataLine.Split(',');
        Assert.Equal("", fields[5]); // TotalRevenue
        Assert.Equal("", fields[6]); // TotalCarrierPayable
        Assert.Equal("", fields[7]); // TotalGrossMargin
        Assert.Equal("", fields[8]); // GrossMarginPercent
        Assert.Equal("", fields[9]); // TotalUnpaidBalance
    }

    [Fact]
    public void Known_totals_render_as_plain_invariant_culture_numbers()
    {
        var csv = MarginRollupCsvWriter.Write(new MarginRollupResponse
        {
            GroupBy = RollupGroupBy.Customer,
            Rows = [Row(totalRevenue: 1234.5m, totalGrossMargin: 300m, grossMarginPercent: 24.3m)],
        });

        var fields = csv.Split("\r\n")[1].Split(',');
        Assert.Equal("1234.5", fields[5]);
        Assert.Equal("300", fields[7]);
        Assert.Equal("24.3", fields[8]);
    }

    [Fact]
    public void Label_containing_a_comma_is_quoted()
    {
        // Lane labels are literally "City, ST -> City, ST" — commas are the common case, not an edge case.
        var csv = MarginRollupCsvWriter.Write(new MarginRollupResponse
        {
            GroupBy = RollupGroupBy.Lane,
            Rows = [Row(label: "Dallas, TX → Atlanta, GA")],
        });

        var dataLine = csv.Split("\r\n")[1];
        Assert.Contains("\"Dallas, TX → Atlanta, GA\"", dataLine);
    }

    [Fact]
    public void Label_containing_a_quote_is_escaped_by_doubling()
    {
        var csv = MarginRollupCsvWriter.Write(new MarginRollupResponse
        {
            GroupBy = RollupGroupBy.Customer,
            Rows = [Row(label: "Acme \"Prime\" Freight")],
        });

        var dataLine = csv.Split("\r\n")[1];
        Assert.Contains("\"Acme \"\"Prime\"\" Freight\"", dataLine);
    }

    [Fact]
    public void Label_without_special_characters_is_not_quoted()
    {
        var csv = MarginRollupCsvWriter.Write(new MarginRollupResponse
        {
            GroupBy = RollupGroupBy.Customer,
            Rows = [Row(label: "Acme")],
        });

        var fields = csv.Split("\r\n")[1].Split(',');
        Assert.Equal("Acme", fields[2]);
    }

    [Fact]
    public void Truncated_flag_is_repeated_on_every_row()
    {
        var csv = MarginRollupCsvWriter.Write(new MarginRollupResponse
        {
            GroupBy = RollupGroupBy.Customer,
            Rows = [Row(key: "A"), Row(key: "B")],
            Truncated = true,
        });

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length); // header + 2 rows
        Assert.EndsWith(",True", lines[1]);
        Assert.EndsWith(",True", lines[2]);
    }

    [Fact]
    public void GroupBy_is_repeated_on_every_row()
    {
        var csv = MarginRollupCsvWriter.Write(new MarginRollupResponse
        {
            GroupBy = RollupGroupBy.Rep,
            Rows = [Row(key: "A"), Row(key: "B")],
        });

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Rep,", lines[1]);
        Assert.StartsWith("Rep,", lines[2]);
    }
}
