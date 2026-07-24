using System.Globalization;
using System.Text;

namespace LtlTool.Api.Features.Ltl.Reporting;

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
                Escape(row.Id),
                Escape(row.LoadId),
                Escape(row.LoadNumber ?? ""),
                Escape(row.TripId ?? ""),
                row.EntityType.ToString(),
                Escape(row.Type ?? ""),
                Escape(row.Description ?? ""),
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

    private static string Escape(string value)
    {
        if (value.IndexOfAny([',', '"', '\n', '\r']) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
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
                Escape(row.Id),
                Escape(row.LoadId),
                Escape(row.LoadNumber ?? ""),
                Escape(row.TripId ?? ""),
                Escape(row.Status ?? ""),
                Escape(row.CarrierId ?? ""),
                Escape(row.CarrierName ?? ""),
                Escape(row.Driver1Id ?? ""),
                Escape(row.Driver1Name ?? ""),
                Escape(row.Driver2Id ?? ""),
                Escape(row.Driver2Name ?? ""),
                Escape(row.OwnerOperatorId ?? ""),
                Escape(row.OwnerOperatorName ?? ""),
                Escape(row.TruckId ?? ""),
                Escape(row.TrailerId ?? ""),
                Escape(row.DispatcherId ?? ""),
                Escape(row.DispatchedBy ?? ""),
                row.CarrierAssignedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "",
                row.CapturedAt.ToString("O", CultureInfo.InvariantCulture),
            };
            sb.Append(string.Join(",", fields)).Append("\r\n");
        }

        return sb.ToString();
    }

    private static string Escape(string value)
    {
        if (value.IndexOfAny([',', '"', '\n', '\r']) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
