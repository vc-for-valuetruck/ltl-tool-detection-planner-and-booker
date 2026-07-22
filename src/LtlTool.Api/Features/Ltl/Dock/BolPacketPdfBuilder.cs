using System.Globalization;
using LtlTool.Api.Features.Ltl.Consolidation;

namespace LtlTool.Api.Features.Ltl.Dock;

/// <summary>
/// Renders a downloadable server-side PDF of the combined BOL packet / dock manifest — the same
/// content the print-CSS <c>print-bol</c> view shows (see <c>web/src/app/features/ltl/dock.html</c>):
/// the parent marked BOL CONTROLLING, each sibling annotated with the zeroed-miles / LTL=true /
/// Main Load Id compliance note, combined driver economics, and the honest "—" / no-fabrication
/// footnote. Read-only: it renders a plan that was built read-only against Alvys and records nothing.
/// </summary>
public sealed class BolPacketPdfBuilder
{
    /// <summary>
    /// Builds the PDF bytes for a consolidation plan. <paramref name="warehouse"/> and
    /// <paramref name="auditContext"/> are optional header context (yard name, audit id/timestamp);
    /// when absent the header degrades honestly rather than fabricating values.
    /// </summary>
    public byte[] Build(
        ConsolidationPlanResponse plan,
        WarehouseSummary? warehouse = null,
        BolPacketAuditContext? auditContext = null)
    {
        var doc = new SimplePdfDocument();

        doc.Heading("Combined BOL Packet · Dock Manifest", 16);

        var stamp = auditContext?.RecordedAt is { } at
            ? at.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
            : "—";
        var auditId = string.IsNullOrWhiteSpace(auditContext?.AuditId) ? "—" : auditContext!.AuditId;
        var yardName = string.IsNullOrWhiteSpace(warehouse?.Name) ? "—" : warehouse!.Name;
        doc.Line($"Audit {auditId} · {stamp} · {yardName}", 9);

        if (warehouse is not null &&
            (!string.IsNullOrWhiteSpace(warehouse.AddressLabel) || !string.IsNullOrWhiteSpace(warehouse.LocationType)))
        {
            var loc = string.IsNullOrWhiteSpace(warehouse.LocationType) ? "Location" : warehouse.LocationType!;
            var addr = string.IsNullOrWhiteSpace(warehouse.AddressLabel) ? "" : $" · {warehouse.AddressLabel}";
            doc.Line($"{loc}{addr}", 9);
            doc.Line("Location detail from the Alvys dispatch planner (read-only).", 8);
        }

        var writeback = string.IsNullOrWhiteSpace(auditContext?.AlvysWriteback)
            ? "NotPerformed"
            : auditContext!.AlvysWriteback;
        doc.Line($"Read-only record — no Alvys writeback performed ({writeback})", 9);
        doc.Gap(8);

        var mainLoadId = plan.ClickCard.MainLoadIdReferenceValue;
        var headers = new[] { "Role", "Load", "Customer", "Consolidation policy", "LTL", "Main Load Id" };
        var widths = new double[] { 1.4, 1.2, 1.6, 1.9, 0.5, 1.4 };

        var rows = new List<IReadOnlyList<string>>
        {
            new[]
            {
                "PARENT · BOL CONTROLLING",
                plan.Parent.LoadNumber ?? plan.Parent.Id,
                plan.Parent.CustomerName ?? "—",
                "—",
                "true",
                mainLoadId,
            },
        };

        foreach (var s in plan.Siblings)
        {
            rows.Add(new[]
            {
                "CHILD (miles zeroed)",
                s.LoadNumber ?? s.LoadId,
                s.CustomerName ?? "—",
                TierLabel(s.CustomerTier),
                "true",
                mainLoadId,
            });
        }

        doc.Table(headers, rows, widths);
        doc.Gap(10);

        doc.Line(
            $"Combined driver value: {FormatCurrency(plan.CombinedDriverTripValue)}   ·   " +
            $"Driver loaded miles: {FormatMiles(plan.DriverLoadedMiles)}   ·   " +
            $"Combined driver RPM: {FormatRpm(plan.CombinedRevenuePerMile)}",
            10,
            bold: true);
        doc.Gap(10);

        doc.Line(
            "Sibling loaded miles ride the parent linehaul (set to 0 in Alvys) so the driver is not",
            9);
        doc.Line(
            "paid twice. Verify weight, pallet count, and receiver requirements at the dock. Values",
            9);
        doc.Line(
            "shown as \"—\" were not supplied by Alvys and were not fabricated.",
            9);

        return doc.Build();
    }

    private static string TierLabel(CustomerConsolidationTier tier) => tier switch
    {
        CustomerConsolidationTier.Allowed => "Consolidation allowed",
        CustomerConsolidationTier.NotifyRequired => "Notify customer",
        CustomerConsolidationTier.Never => "Never consolidate",
        _ => "Confirm with account owner",
    };

    private static string FormatCurrency(decimal? value) =>
        value is null ? "—" : "$" + Math.Round(value.Value, 0).ToString("#,##0", CultureInfo.InvariantCulture);

    private static string FormatRpm(decimal? value) =>
        value is null ? "—" : "$" + value.Value.ToString("0.00", CultureInfo.InvariantCulture) + " / mi";

    private static string FormatMiles(decimal? value) =>
        value is null ? "—" : value.Value.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>
/// Optional header context for a rendered BOL packet: the internal audit id / timestamp / writeback
/// status. All fields optional so a preview (no audit yet) still renders an honest header.
/// </summary>
public sealed record BolPacketAuditContext(
    string? AuditId = null,
    DateTimeOffset? RecordedAt = null,
    string? AlvysWriteback = null);
