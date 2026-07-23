using System.Globalization;
using LtlTool.Api.Features.Ltl.Dock;

namespace LtlTool.Api.Features.Ltl.Billing;

/// <summary>
/// Renders a downloadable, professional invoice PDF for an assembled invoice, reusing the same
/// dependency-free <see cref="SimplePdfDocument"/> writer the dock BOL packet uses. Shows the
/// invoice header, the parent + sibling loads with their BOL status and line totals, the per-load
/// charge breakdown, the combined economics (total + driver-RPM), and the honest
/// "no Alvys writeback performed" footnote. Missing values render as "—", never fabricated.
/// </summary>
public sealed class InvoicePdfBuilder
{
    public byte[] Build(InvoiceView invoice)
    {
        var doc = new SimplePdfDocument();

        doc.Heading("INVOICE", 18);
        doc.Line($"Invoice {invoice.InvoiceNumber} · {invoice.Status}", 11, bold: true);

        var customer = string.IsNullOrWhiteSpace(invoice.CustomerName) ? "—" : invoice.CustomerName!;
        doc.Line($"Bill to: {customer}", 10);
        var parentLoad = string.IsNullOrWhiteSpace(invoice.ParentLoadNumber) ? "—" : invoice.ParentLoadNumber!;
        var corridor = string.IsNullOrWhiteSpace(invoice.CorridorCode) ? "—" : invoice.CorridorCode!;
        doc.Line($"Parent load: {parentLoad} · Corridor: {corridor}", 10);
        doc.Line($"Created {Stamp(invoice.CreatedAt)} by {invoice.CreatedBy}", 9);
        if (invoice.FinalizedAt is { } finalizedAt)
            doc.Line($"Finalized {Stamp(finalizedAt)} by {invoice.FinalizedBy ?? "—"}", 9);
        doc.Line($"Read-only record — no Alvys writeback performed ({invoice.AlvysWriteback})", 9);
        doc.Gap(10);

        // Load summary table.
        doc.Heading("Loads", 12);
        var headers = new[] { "Role", "Load", "Customer", "BOL", "Loaded mi", "Line total" };
        var widths = new double[] { 1.0, 1.3, 1.9, 1.0, 1.0, 1.2 };
        var rows = new List<IReadOnlyList<string>>();
        foreach (var line in invoice.Loads)
        {
            rows.Add(new[]
            {
                line.IsParent ? "PARENT" : "SIBLING",
                line.LoadNumber ?? line.LoadId,
                string.IsNullOrWhiteSpace(line.CustomerName) ? "—" : line.CustomerName!,
                line.BolPresent ? "On file" : "MISSING",
                line.LoadedMiles is { } mi ? mi.ToString("0.#", CultureInfo.InvariantCulture) : "—",
                Money(line.LineTotal),
            });
        }
        doc.Table(headers, rows, widths);
        doc.Gap(8);

        var missing = invoice.LoadsMissingBol;
        if (missing.Count > 0)
        {
            doc.Line($"BOL missing on {missing.Count} load(s): {string.Join(", ", missing)} — resolve before billing.", 9, bold: true);
            doc.Gap(6);
        }

        // Charge breakdown per load.
        doc.Heading("Charges", 12);
        var chargeHeaders = new[] { "Load", "Type", "Description", "Amount" };
        var chargeWidths = new double[] { 1.3, 1.2, 2.6, 1.1 };
        var chargeRows = new List<IReadOnlyList<string>>();
        foreach (var line in invoice.Loads)
        {
            var loadLabel = line.LoadNumber ?? line.LoadId;
            if (line.Charges.Count == 0)
            {
                chargeRows.Add(new[] { loadLabel, "—", "No charges recorded", "—" });
                continue;
            }
            foreach (var charge in line.Charges)
            {
                chargeRows.Add(new[]
                {
                    loadLabel,
                    charge.Type.ToString(),
                    string.IsNullOrWhiteSpace(charge.Description) ? "—" : charge.Description!,
                    Money(charge.Amount),
                });
            }
        }
        doc.Table(chargeHeaders, chargeRows, chargeWidths);
        doc.Gap(10);

        // Totals.
        doc.Heading("Totals", 12);
        doc.Line($"Invoice total: {Money(invoice.InvoiceTotal)}", 11, bold: true);
        doc.Line($"Combined driver trip value: {NullableMoney(invoice.CombinedDriverTripValue)}", 10);
        doc.Line($"Parent driver loaded miles: {NullableMiles(invoice.DriverLoadedMiles)}", 10);
        doc.Line($"Combined driver RPM: {NullableMoney(invoice.CombinedRevenuePerMile)}", 10);
        doc.Gap(6);
        doc.Line(
            "Driver RPM = combined driver trip value / parent driver loaded miles. Shown only when " +
            "both inputs are known; otherwise reported as — (never estimated).", 8);

        return doc.Build();
    }

    private static string Stamp(DateTimeOffset at) =>
        at.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string Money(decimal value) =>
        value.ToString("C", CultureInfo.GetCultureInfo("en-US"));

    private static string NullableMoney(decimal? value) =>
        value is { } v ? Money(v) : "—";

    private static string NullableMiles(decimal? value) =>
        value is { } v ? v.ToString("0.#", CultureInfo.InvariantCulture) : "—";
}
