using System.Text.Json;
using System.Text.Json.Serialization;

namespace LtlTool.Api.Features.Ltl.Bol;

/// <summary>
/// A field the BOL reader can suggest from a load's Bill of Lading document. Every field is a
/// <b>suggestion only</b>: it is presented to a human for review and is NEVER auto-applied and NEVER
/// written back to Alvys. The taxonomy is deliberately small and operational — the values that a
/// dispatcher/biller has to reconcile against a paper BOL to bill accurately.
///
/// <para>Guardrail: an accepted suggestion annotates the tool's own internal surfaces only. It is not
/// an operational value from Alvys and must never be treated as one.</para>
/// </summary>
public enum BolField
{
    /// <summary>Number of pallets stated on the BOL.</summary>
    PalletCount,

    /// <summary>Total handling-unit / piece count stated on the BOL.</summary>
    PieceCount,

    /// <summary>Gross/total weight stated on the BOL (value + unit are kept verbatim; not converted).</summary>
    Weight,

    /// <summary>NMFC freight class stated on the BOL (e.g. 50, 92.5, 175).</summary>
    FreightClass,

    /// <summary>Commodity / description-of-goods text stated on the BOL.</summary>
    CommodityDescription,

    /// <summary>Whether the BOL marks the shipment as hazardous material.</summary>
    HazmatFlag,
}

/// <summary>Lifecycle of a BOL field suggestion in the internal review queue.</summary>
public enum BolSuggestionStatus
{
    /// <summary>Extracted, awaiting a human accept/reject decision. Never applied while Pending.</summary>
    Pending,

    /// <summary>Accepted by a human — annotates internal surfaces only. Never writes to Alvys.</summary>
    Accepted,

    /// <summary>Rejected — kept for audit, annotates nothing.</summary>
    Rejected,
}

/// <summary>
/// A single field suggested by an <see cref="IBolFieldExtractor"/>, before persistence. The
/// <see cref="EvidenceQuote"/> is mandatory and must be a verbatim excerpt of the extracted document
/// text — the read service rejects the whole read if any suggested field lacks a real quote
/// (fail-closed: no partial suggestions, no silent drops).
/// </summary>
public sealed class ExtractedBolField
{
    public required BolField Field { get; init; }

    /// <summary>
    /// The suggested value as read from the document, kept as a string so nothing is coerced or
    /// unit-converted (a weight stays "12,480 lbs", a class stays "92.5"). Missing is never invented.
    /// </summary>
    public required string Value { get; init; }

    /// <summary>Verbatim snippet from the extracted document text that justifies this field. Mandatory.</summary>
    public required string EvidenceQuote { get; init; }

    /// <summary>0.0–1.0 extraction confidence (NOT an operational number). Deterministic matches are lower-boosted.</summary>
    public double Confidence { get; init; } = 0.5;
}

/// <summary>
/// Durable row for one suggested BOL field awaiting human review. Internal data — never read from or
/// written to Alvys. Carries the source document identity so an accepted value is auditable back to
/// the exact document it came from (who accepted, when, from which document).
/// </summary>
public sealed class BolFieldSuggestionRecord
{
    public required string Id { get; set; }

    /// <summary>Alvys load number the BOL belongs to (from the request, not invented).</summary>
    public required string LoadNumber { get; set; }

    /// <summary>Alvys document id the suggestion was read from — the audit anchor for an accepted value.</summary>
    public required string DocumentId { get; set; }

    /// <summary>Human-readable document name/path, when Alvys supplied one.</summary>
    public string? DocumentName { get; set; }

    /// <summary>Field type, stored as a readable string so the table is legible in the database.</summary>
    public required string Field { get; set; }

    /// <summary>Suggested value, verbatim from the document. Never coerced to 0/false/"good".</summary>
    public required string Value { get; set; }

    public double Confidence { get; set; }

    /// <summary>Verbatim excerpt from the document text. Mandatory — a suggestion without it is never stored.</summary>
    public required string EvidenceQuote { get; set; }

    /// <summary>Name of the extractor that produced this suggestion, surfaced for honesty/audit.</summary>
    public required string ExtractorName { get; set; }

    /// <summary>Status, stored as a readable string (Pending / Accepted / Rejected).</summary>
    public required string Status { get; set; }

    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When a human accepted/rejected the suggestion; null while Pending.</summary>
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecidedBy { get; set; }
}

/// <summary>Read-model of a stored BOL field suggestion for the SPA.</summary>
public sealed record BolFieldSuggestionView(
    string Id,
    string LoadNumber,
    string DocumentId,
    string? DocumentName,
    BolField Field,
    string Value,
    double Confidence,
    string EvidenceQuote,
    string ExtractorName,
    BolSuggestionStatus Status,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt,
    string? DecidedBy);

/// <summary>Read response: the suggestions produced from one BOL read. Empty list is a valid outcome.</summary>
public sealed record BolReadResponse(
    string LoadNumber,
    string DocumentId,
    string ExtractorName,
    int Count,
    IReadOnlyList<BolFieldSuggestionView> Suggestions);

/// <summary>Query filter for listing stored BOL suggestions.</summary>
public sealed record BolSuggestionQuery(
    string? LoadNumber = null,
    BolSuggestionStatus? Status = null,
    int Max = 200);

/// <summary>
/// Thrown when a BOL read must fail closed: the document could not be fetched, no text could be
/// extracted, the extractor errored, or a suggested field lacked a verbatim evidence quote. The
/// controller maps this to a legible error and nothing is persisted — no partial suggestions.
/// </summary>
public sealed class BolReadException(string message) : Exception(message);

/// <summary>Shared serialization/mapping helpers so the store, service and tests stay consistent.</summary>
public static class BolMapping
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static BolFieldSuggestionView ToView(BolFieldSuggestionRecord r) => new(
        r.Id,
        r.LoadNumber,
        r.DocumentId,
        r.DocumentName,
        Enum.Parse<BolField>(r.Field),
        r.Value,
        r.Confidence,
        r.EvidenceQuote,
        r.ExtractorName,
        Enum.Parse<BolSuggestionStatus>(r.Status),
        r.CreatedBy,
        r.CreatedAt,
        r.DecidedAt,
        r.DecidedBy);
}
