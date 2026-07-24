using System.Globalization;
using System.Text;

namespace LtlTool.Api.Features.Ltl.Reporting;

/// <summary>
/// Shared RFC 4180 field formatting for the reporting CSV writers, plus CSV-injection hardening:
/// a cell whose value starts with <c>=</c>, <c>+</c>, <c>-</c>, or <c>@</c> is prefixed with a
/// leading apostrophe before quoting. Spreadsheet applications (Excel, Sheets) treat those leading
/// characters as a formula and can evaluate them on open — since these fields carry upstream
/// Alvys/Yard text (descriptions, party names) this tool never controls, a value starting with one
/// of those characters must never be handed to a spreadsheet as live formula input. The apostrophe
/// itself renders invisibly in spreadsheet UIs (it forces "treat as text") and round-trips as a
/// normal character to any non-spreadsheet CSV consumer (Power BI's Text/CSV connector included).
/// </summary>
internal static class CsvCell
{
    private static readonly char[] FormulaLeadChars = ['=', '+', '-', '@'];

    public static string Escape(string value)
    {
        var safe = FormulaLeadChars.Contains(value.Length > 0 ? value[0] : '\0') ? "'" + value : value;
        if (safe.IndexOfAny([',', '"', '\n', '\r']) < 0) return safe;
        return "\"" + safe.Replace("\"", "\"\"") + "\"";
    }
}

/// <summary>
/// Renders <see cref="AccessorialRecord"/> rows as RFC 4180 CSV for external reporting tools (e.g.
/// Power BI's Text/CSV connector). Same read-only, Alvys-derived data the JSON endpoint returns —
/// only the response shape changes. A missing value is written as an empty cell, never "0" or a
/// blank-string guess, so a BI report built on this feed cannot mistake "unknown" for a real value.
/// </summary>
public static class AccessorialHistoryCsvWriter
{
    private static readonly string[] Header =
    [
        "Id", "LoadId", "LoadNumber", "TripId", "EntityType", "Type", "Description", "Amount",
        "FirstSeenAt", "LastSeenAt",
    ];

    public static string Write(IReadOnlyList<AccessorialRecord> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", Header)).Append("\r\n");

        foreach (var row in rows)
        {
            var fields = new[]
            {
                CsvCell.Escape(row.Id),
                CsvCell.Escape(row.LoadId),
                CsvCell.Escape(row.LoadNumber ?? ""),
                CsvCell.Escape(row.TripId ?? ""),
                row.EntityType.ToString(),
                CsvCell.Escape(row.Type ?? ""),
                CsvCell.Escape(row.Description ?? ""),
                FormatDecimal(row.Amount),
                row.FirstSeenAt.ToString("O", CultureInfo.InvariantCulture),
                row.LastSeenAt.ToString("O", CultureInfo.InvariantCulture),
            };
            sb.Append(string.Join(",", fields)).Append("\r\n");
        }

        return sb.ToString();
    }

    private static string FormatDecimal(decimal? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "";
}

/// <summary>
/// Renders <see cref="LoadAssignmentRecord"/> rows as RFC 4180 CSV for external reporting tools.
/// Same posture as <see cref="AccessorialHistoryCsvWriter"/>: read-only, Alvys-derived, missing
/// values are empty cells, never fabricated.
/// </summary>
public static class LoadAssignmentHistoryCsvWriter
{
    private static readonly string[] Header =
    [
        "Id", "LoadId", "LoadNumber", "TripId", "Status", "CarrierId", "CarrierName",
        "Driver1Id", "Driver1Name", "Driver2Id", "Driver2Name", "OwnerOperatorId", "OwnerOperatorName",
        "TruckId", "TrailerId", "DispatcherId", "DispatchedBy", "CarrierAssignedAt", "CapturedAt",
    ];

    public static string Write(IReadOnlyList<LoadAssignmentRecord> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", Header)).Append("\r\n");

        foreach (var row in rows)
        {
            var fields = new[]
            {
                CsvCell.Escape(row.Id),
                CsvCell.Escape(row.LoadId),
                CsvCell.Escape(row.LoadNumber ?? ""),
                CsvCell.Escape(row.TripId ?? ""),
                CsvCell.Escape(row.Status ?? ""),
                CsvCell.Escape(row.CarrierId ?? ""),
                CsvCell.Escape(row.CarrierName ?? ""),
                CsvCell.Escape(row.Driver1Id ?? ""),
                CsvCell.Escape(row.Driver1Name ?? ""),
                CsvCell.Escape(row.Driver2Id ?? ""),
                CsvCell.Escape(row.Driver2Name ?? ""),
                CsvCell.Escape(row.OwnerOperatorId ?? ""),
                CsvCell.Escape(row.OwnerOperatorName ?? ""),
                CsvCell.Escape(row.TruckId ?? ""),
                CsvCell.Escape(row.TrailerId ?? ""),
                CsvCell.Escape(row.DispatcherId ?? ""),
                CsvCell.Escape(row.DispatchedBy ?? ""),
                row.CarrierAssignedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "",
                row.CapturedAt.ToString("O", CultureInfo.InvariantCulture),
            };
            sb.Append(string.Join(",", fields)).Append("\r\n");
        }

        return sb.ToString();
    }
}
