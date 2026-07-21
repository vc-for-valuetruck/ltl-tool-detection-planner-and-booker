using System.Globalization;
using System.Text;

namespace LtlTool.Api.Features.Ltl;

/// <summary>
/// Renders a <see cref="MarginRollupResponse"/> as RFC 4180 CSV for external reporting tools
/// (e.g. Power BI's Text/CSV connector). This only changes the response shape — the data is the
/// same read-only, Alvys-derived margin rollup the JSON endpoint returns; nothing new is ingested
/// and nothing is written back to Alvys. A missing total is written as an empty cell, never "0",
/// so a BI report built on this feed cannot mistake "unknown" for a real zero.
/// </summary>
public static class MarginRollupCsvWriter
{
    private static readonly string[] Header =
    [
        "GroupBy", "Key", "Label", "LabelIsId", "LoadCount", "TotalRevenue",
        "TotalCarrierPayable", "TotalGrossMargin", "GrossMarginPercent",
        "TotalUnpaidBalance", "ExceptionCount", "ReadyToBillCount", "Truncated",
    ];

    public static string Write(MarginRollupResponse response)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", Header)).Append("\r\n");

        // Truncated is repeated per row (rather than a leading comment line) so the file stays
        // strict, single-header-row CSV that every BI/spreadsheet parser reads without special-casing.
        var groupBy = response.GroupBy.ToString();
        var truncated = response.Truncated.ToString();

        foreach (var row in response.Rows)
        {
            var fields = new[]
            {
                Escape(groupBy),
                Escape(row.Key),
                Escape(row.Label),
                row.LabelIsId.ToString(),
                row.LoadCount.ToString(CultureInfo.InvariantCulture),
                FormatDecimal(row.TotalRevenue),
                FormatDecimal(row.TotalCarrierPayable),
                FormatDecimal(row.TotalGrossMargin),
                FormatDecimal(row.GrossMarginPercent),
                FormatDecimal(row.TotalUnpaidBalance),
                row.ExceptionCount.ToString(CultureInfo.InvariantCulture),
                row.ReadyToBillCount.ToString(CultureInfo.InvariantCulture),
                truncated,
            };
            sb.Append(string.Join(",", fields)).Append("\r\n");
        }

        return sb.ToString();
    }

    private static string FormatDecimal(decimal? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "";

    /// <summary>Quotes a field per RFC 4180 when it contains a comma, quote, or newline.</summary>
    private static string Escape(string value)
    {
        if (value.IndexOfAny([',', '"', '\n', '\r']) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
