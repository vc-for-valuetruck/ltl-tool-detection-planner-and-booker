using System.Text.Json;
using LtlTool.Api.Features.Integrations.Alvys.Writeback;

namespace LtlTool.Api.Features.Ltl.Billing;

/// <summary>Thrown when an edit/finalize is attempted on an already-final invoice.</summary>
public sealed class InvoiceLockedException(string invoiceId)
    : InvalidOperationException($"Invoice '{invoiceId}' is final and can no longer be edited.")
{
    public string InvoiceId { get; } = invoiceId;
}

/// <summary>
/// The Invoice Studio: assembles a customer invoice from a consolidation (parent + sibling loads),
/// keeps per-load charges editable, computes totals + combined driver-RPM, tracks BOL presence per
/// load, and persists drafts with an edit history. It also builds the <i>exact contracted</i> Alvys
/// write payloads for the invoice through the shared writeback seam — but only as previews:
/// every payload resolves to <c>AuditOnly</c>/<c>NotPerformed</c> because writeback mode is Disabled
/// by default and production execution is separately gated. Enabling that gate later reuses the same
/// requests unchanged (no rework).
///
/// <para>Alvys posture: <b>read-only</b>. This service never reads operational data from any non-Alvys
/// system and never pushes to Alvys. Load/BOL/charge inputs are supplied by the caller (sourced from
/// the tool's Alvys-derived consolidation + BOL surfaces); missing data is surfaced, never invented.</para>
/// </summary>
public sealed class InvoiceStudioService(
    IInvoiceStore store,
    IAlvysWriteGateway writeGateway,
    InvoicePdfBuilder pdfBuilder,
    TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public InvoiceView Assemble(AssembleInvoiceRequest request, string user)
    {
        var now = clock.GetUtcNow();
        var lines = BuildLines(request.Loads);
        var parent = lines.FirstOrDefault(l => l.IsParent);

        var invoiceNumber = string.IsNullOrWhiteSpace(request.InvoiceNumber)
            ? DeriveInvoiceNumber(parent, now)
            : request.InvoiceNumber.Trim();

        var history = new List<InvoiceEditEvent>
        {
            new()
            {
                At = now,
                By = user,
                Action = "Created",
                Detail = $"Assembled {lines.Count} load(s); total {lines.Sum(l => l.LineTotal):C}.",
            },
        };

        var record = new InvoiceRecord
        {
            Id = Guid.NewGuid().ToString("n"),
            InvoiceNumber = invoiceNumber,
            Status = InvoiceStatus.Draft,
            CorridorCode = request.CorridorCode,
            CustomerId = request.CustomerId,
            CustomerName = request.CustomerName ?? parent?.CustomerName,
            ParentLoadId = parent?.LoadId,
            ParentLoadNumber = parent?.LoadNumber,
            Notes = request.Notes,
            LoadsJson = JsonSerializer.Serialize(lines, Json),
            EditHistoryJson = JsonSerializer.Serialize(history, Json),
            CreatedBy = user,
            CreatedAt = now,
            UpdatedBy = user,
            UpdatedAt = now,
        };
        ApplyComputed(record, lines);

        store.Add(record);
        return ToView(record);
    }

    public InvoiceView? Update(string id, UpdateInvoiceRequest request, string user)
    {
        var record = store.Get(id);
        if (record is null) return null;
        if (record.Status == InvoiceStatus.Final) throw new InvoiceLockedException(id);

        var now = clock.GetUtcNow();
        var lines = BuildLines(request.Loads);
        var parent = lines.FirstOrDefault(l => l.IsParent);
        var history = ReadHistory(record);
        history.Add(new InvoiceEditEvent
        {
            At = now,
            By = user,
            Action = "Edited",
            Detail = $"Updated to {lines.Count} load(s); total {lines.Sum(l => l.LineTotal):C}.",
        });

        record.Notes = request.Notes ?? record.Notes;
        record.CustomerName ??= parent?.CustomerName;
        record.ParentLoadId = parent?.LoadId ?? record.ParentLoadId;
        record.ParentLoadNumber = parent?.LoadNumber ?? record.ParentLoadNumber;
        record.LoadsJson = JsonSerializer.Serialize(lines, Json);
        record.EditHistoryJson = JsonSerializer.Serialize(history, Json);
        record.UpdatedBy = user;
        record.UpdatedAt = now;
        ApplyComputed(record, lines);

        store.Update(record);
        return ToView(record);
    }

    public InvoiceView? Finalize(string id, string user)
    {
        var record = store.Get(id);
        if (record is null) return null;
        if (record.Status == InvoiceStatus.Final) return ToView(record);

        var now = clock.GetUtcNow();
        var history = ReadHistory(record);
        history.Add(new InvoiceEditEvent { At = now, By = user, Action = "Finalized", Detail = null });

        record.Status = InvoiceStatus.Final;
        record.FinalizedAt = now;
        record.FinalizedBy = user;
        record.UpdatedBy = user;
        record.UpdatedAt = now;
        record.EditHistoryJson = JsonSerializer.Serialize(history, Json);

        store.Update(record);
        return ToView(record);
    }

    public InvoiceView? Get(string id)
    {
        var record = store.Get(id);
        return record is null ? null : ToView(record);
    }

    public IReadOnlyList<InvoiceSummary> List(string? parentLoadId, InvoiceStatus? status, int max) =>
        store.List(parentLoadId, status, max <= 0 ? 50 : max)
            .Select(ToSummary)
            .ToArray();

    public byte[]? BuildPdf(string id)
    {
        var record = store.Get(id);
        return record is null ? null : pdfBuilder.Build(ToView(record));
    }

    /// <summary>
    /// Builds the exact contracted Alvys write payloads for this invoice as previews. Nothing is sent:
    /// each outcome reflects the configured writeback mode (Disabled → AuditOnly by default), so the
    /// invoice stays <c>NotPerformed</c>. Three Public-API operations are relevant to a customer
    /// invoice:
    /// <list type="bullet">
    /// <item><c>load-update</c> — PATCH the parent load's OrderNumber to this invoice number (needs an
    /// If-Match ETag; without one the preview honestly shows the blocker rather than fabricating a token).</item>
    /// <item><c>upload-load-document</c> — attach the generated invoice PDF to the parent load as a
    /// Bill of Lading document.</item>
    /// <item><c>create-customer-payment</c> — post the invoice total as a customer payment
    /// (idempotent on the invoice number).</item>
    /// </list>
    /// Returns null when the invoice does not exist.
    /// </summary>
    public IReadOnlyList<AlvysOperationOutcome>? BuildAlvysPreviews(string id, string? parentLoadEtag)
    {
        var record = store.Get(id);
        if (record is null) return null;

        var view = ToView(record);
        var loadNumber = view.ParentLoadNumber;
        var pdf = pdfBuilder.Build(view);

        var previews = new List<AlvysOperationOutcome>
        {
            writeGateway.DryRun("load-update", new AlvysOperationRequest
            {
                LoadNumber = loadNumber,
                Etag = parentLoadEtag,
                Fields = new Dictionary<string, string?>
                {
                    [AlvysLoadUpdateFields.OrderNumber] = Truncate(view.InvoiceNumber, AlvysLoadUpdateFields.OrderNumberMaxLength),
                },
            }),
            writeGateway.DryRun("upload-load-document", new AlvysOperationRequest
            {
                LoadNumber = loadNumber,
                DocumentType = "Bill of Lading",
                FileBytes = pdf,
                FileName = $"{view.InvoiceNumber}.pdf",
                ContentType = "application/pdf",
            }),
            writeGateway.DryRun("create-customer-payment", new AlvysOperationRequest
            {
                LoadNumber = loadNumber,
                PaymentAmount = view.InvoiceTotal,
                PaymentCurrency = "USD",
                PaymentDate = clock.GetUtcNow(),
                ReferenceNumber = view.InvoiceNumber,
            }),
        };

        return previews;
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static List<InvoiceLoadLine> BuildLines(IEnumerable<InvoiceLoadInput> inputs) =>
        inputs.Select(i => new InvoiceLoadLine
        {
            LoadId = string.IsNullOrWhiteSpace(i.LoadId) ? Guid.NewGuid().ToString("n") : i.LoadId.Trim(),
            LoadNumber = i.LoadNumber,
            IsParent = i.IsParent,
            CustomerName = i.CustomerName,
            Status = i.Status,
            AlvysLoadUrl = i.AlvysLoadUrl,
            BolPresent = i.BolPresent,
            BolArtifactId = i.BolArtifactId,
            LoadedMiles = i.LoadedMiles,
            DriverTripRate = i.DriverTripRate,
            Charges = i.Charges.Select(c => new InvoiceCharge
            {
                Id = Guid.NewGuid().ToString("n"),
                Type = c.Type,
                Description = c.Description,
                Amount = c.Amount,
            }).ToArray(),
        }).ToList();

    private static void ApplyComputed(InvoiceRecord record, IReadOnlyList<InvoiceLoadLine> lines)
    {
        var parent = lines.FirstOrDefault(l => l.IsParent);

        record.InvoiceTotal = lines.Sum(l => l.LineTotal);
        record.CombinedRevenue = record.InvoiceTotal;

        // Combined driver-RPM: sum of driver trip rates ÷ parent's driver loaded miles. Both must be
        // known or the RPM stays null — never guessed (mirrors ConsolidationPlanResponse).
        var tripRates = lines.Where(l => l.DriverTripRate is not null).Select(l => l.DriverTripRate!.Value).ToArray();
        record.CombinedDriverTripValue = tripRates.Length > 0 ? tripRates.Sum() : null;
        record.DriverLoadedMiles = parent?.LoadedMiles;
        record.CombinedRevenuePerMile =
            record.CombinedDriverTripValue is { } value && record.DriverLoadedMiles is > 0m
                ? decimal.Round(value / record.DriverLoadedMiles.Value, 2)
                : null;

        record.LoadsMissingBolCount = lines.Count(l => !l.BolPresent);
    }

    private static string DeriveInvoiceNumber(InvoiceLoadLine? parent, DateTimeOffset now)
    {
        var suffix = parent?.LoadNumber ?? parent?.LoadId ?? now.ToUnixTimeSeconds().ToString();
        return $"INV-{suffix}";
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private List<InvoiceLoadLine> ReadLines(InvoiceRecord record) =>
        string.IsNullOrWhiteSpace(record.LoadsJson)
            ? []
            : JsonSerializer.Deserialize<List<InvoiceLoadLine>>(record.LoadsJson, Json) ?? [];

    private List<InvoiceEditEvent> ReadHistory(InvoiceRecord record) =>
        string.IsNullOrWhiteSpace(record.EditHistoryJson)
            ? []
            : JsonSerializer.Deserialize<List<InvoiceEditEvent>>(record.EditHistoryJson, Json) ?? [];

    private InvoiceView ToView(InvoiceRecord record)
    {
        var lines = ReadLines(record);
        return new InvoiceView
        {
            Id = record.Id,
            InvoiceNumber = record.InvoiceNumber,
            Status = record.Status,
            CorridorCode = record.CorridorCode,
            CustomerId = record.CustomerId,
            CustomerName = record.CustomerName,
            ParentLoadId = record.ParentLoadId,
            ParentLoadNumber = record.ParentLoadNumber,
            Notes = record.Notes,
            Loads = lines,
            EditHistory = ReadHistory(record).OrderByDescending(e => e.At).ToArray(),
            InvoiceTotal = record.InvoiceTotal,
            CombinedRevenue = record.CombinedRevenue,
            CombinedDriverTripValue = record.CombinedDriverTripValue,
            DriverLoadedMiles = record.DriverLoadedMiles,
            CombinedRevenuePerMile = record.CombinedRevenuePerMile,
            LoadsMissingBol = lines.Where(l => !l.BolPresent)
                .Select(l => l.LoadNumber ?? l.LoadId)
                .ToArray(),
            CreatedBy = record.CreatedBy,
            CreatedAt = record.CreatedAt,
            UpdatedBy = record.UpdatedBy,
            UpdatedAt = record.UpdatedAt,
            FinalizedAt = record.FinalizedAt,
            FinalizedBy = record.FinalizedBy,
            AlvysWriteback = record.AlvysWriteback,
        };
    }

    private InvoiceSummary ToSummary(InvoiceRecord record) => new()
    {
        Id = record.Id,
        InvoiceNumber = record.InvoiceNumber,
        Status = record.Status,
        CustomerName = record.CustomerName,
        ParentLoadNumber = record.ParentLoadNumber,
        LoadCount = ReadLines(record).Count,
        LoadsMissingBolCount = record.LoadsMissingBolCount,
        InvoiceTotal = record.InvoiceTotal,
        CombinedRevenuePerMile = record.CombinedRevenuePerMile,
        UpdatedAt = record.UpdatedAt,
        AlvysWriteback = record.AlvysWriteback,
    };
}
