using System.Text.Json;
using System.Text.Json.Serialization;

namespace LtlTool.Api.Features.Ltl.Signals;

/// <summary>
/// Type of a structured LTL signal extracted from unstructured text (a note, an email, a call
/// summary, a transcript). Deterministic keyword/dictionary classification first; an LLM extractor
/// is pluggable behind <see cref="ISignalExtractor"/> but is not required to be wired to a live
/// model. The taxonomy is the ROADMAP Phase 6 set: conversations become typed LTL actions instead
/// of dying in free text.
///
/// <para>Guardrail: a signal is <b>text-only</b>. No member of this enum implies a numeric
/// operational value — revenue, weight, and miles never come from extraction, only from Alvys or
/// config. The <see cref="SignalRecord"/> deliberately carries no numeric operational field.</para>
/// </summary>
public enum SignalType
{
    /// <summary>Evidence an accessorial (detention, layover, lumper, reconsignment…) may be billable.</summary>
    AccessorialEvidence,

    /// <summary>A mention that two or more movements could share a trailer / linehaul.</summary>
    ConsolidationOpportunity,

    /// <summary>Customer stance on consolidation/visibility ("don't split", "OK'd cross-dock").</summary>
    CustomerVisibilityPosture,

    /// <summary>A risk that a load will bill short or late (rate confusion, dispute, terms).</summary>
    BillingRisk,

    /// <summary>A load is running late / stuck / delayed.</summary>
    DelayedLoad,

    /// <summary>Paperwork gap — missing BOL/POD/rate-confirmation referenced in the text.</summary>
    MissingDocs,

    /// <summary>A lane the fleet is not modeled as running that the text says we do / could.</summary>
    NewLane,

    /// <summary>A new pickup/delivery site or facility mentioned.</summary>
    NewSite,

    /// <summary>An equipment need (reefer, liftgate, flatbed…) stated for a customer/lane.</summary>
    EquipmentNeed,

    /// <summary>A contract / rate-agreement signal (renewal, volume commitment, dedicated).</summary>
    ContractSignal,

    /// <summary>A competitor / intermediary reference worth tracking (e.g. an NC consolidation broker).</summary>
    CompetitiveIntel,

    /// <summary>A service issue / complaint that should route to a human.</summary>
    ServiceIssue,

    /// <summary>A suggested contact / role change (new dispatcher, account owner).</summary>
    ContactSuggestion,

    /// <summary>Anything else the operator should review; low-confidence catch-all.</summary>
    Other,
}

/// <summary>
/// The LTL surface a signal suggests acting on. This is the "so what" — it tells the dispatcher
/// where an accepted signal would annotate the workbench. Accepting a signal never mutates Alvys;
/// it only annotates the tool's own internal surfaces.
/// </summary>
public enum LtlSurface
{
    SearchFilter,
    BillingWorklistBadge,
    Exception,
    MatchWarning,
    SavedView,
    AuditNote,
    NextBestAction,
}

/// <summary>Lifecycle of a signal in the internal review queue.</summary>
public enum SignalStatus
{
    /// <summary>Extracted, awaiting a dispatcher accept/reject decision.</summary>
    Pending,

    /// <summary>Accepted — annotates the suggested internal surface. Never writes to Alvys.</summary>
    Accepted,

    /// <summary>Rejected — kept for audit, does not annotate anything.</summary>
    Rejected,
}

/// <summary>
/// A signal produced by an <see cref="ISignalExtractor"/>, before persistence. The
/// <see cref="EvidenceQuote"/> is mandatory and must be a verbatim excerpt of the source text —
/// the ingest service rejects the whole request if any produced signal lacks a real quote
/// (fail-closed: no partial writes, no silent drops).
/// </summary>
public sealed class ExtractedSignal
{
    public required SignalType Type { get; init; }

    /// <summary>Verbatim snippet from the source text that justifies this signal. Mandatory.</summary>
    public required string EvidenceQuote { get; init; }

    /// <summary>Where accepting this signal would annotate the workbench.</summary>
    public required LtlSurface SuggestedSurface { get; init; }

    /// <summary>0.0–1.0 extraction confidence (NOT an operational number). Deterministic matches ≈1.0.</summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>One-line human summary of what was detected. No numeric operational values.</summary>
    public string? Summary { get; init; }
}

/// <summary>
/// Durable row for one extracted LTL signal. Internal data — never read from or written to Alvys.
/// Carries no numeric operational field by design (see <see cref="SignalType"/> guardrail): the only
/// number is <see cref="Confidence"/>, an extraction score, not revenue/weight/miles.
/// </summary>
public sealed class SignalRecord
{
    public required string Id { get; set; }

    /// <summary>What kind of text this came from: <c>note</c> / <c>email</c> / <c>transcript</c> / <c>call</c>.</summary>
    public required string SourceType { get; set; }

    /// <summary>Caller-supplied identifier of the source (message id, transcript id, note id…).</summary>
    public required string SourceId { get; set; }

    /// <summary>Signal type, stored as a readable string so the table is legible in the database.</summary>
    public required string SignalType { get; set; }

    public double Confidence { get; set; }

    /// <summary>Verbatim excerpt from the source text. Mandatory — a signal without it is never stored.</summary>
    public required string EvidenceQuote { get; set; }

    /// <summary>Suggested LTL surface, stored as a readable string.</summary>
    public required string SuggestedSurface { get; set; }

    public string? Summary { get; set; }

    /// <summary>Optional load number the signal relates to (from the ingest request, not invented).</summary>
    public string? LoadNumber { get; set; }

    /// <summary>Status, stored as a readable string (Pending / Accepted / Rejected).</summary>
    public required string Status { get; set; }

    public required string IngestedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When a dispatcher accepted/rejected the signal; null while Pending.</summary>
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecidedBy { get; set; }
}

/// <summary>Ingest request: raw text plus honest source metadata. Numeric fields are not accepted.</summary>
public sealed class SignalIngestRequest
{
    /// <summary>Kind of source text: <c>note</c> / <c>email</c> / <c>transcript</c> / <c>call</c>.</summary>
    public string? SourceType { get; set; }

    /// <summary>Identifier of the source (message id, transcript id…). Required for traceability.</summary>
    public string? SourceId { get; set; }

    /// <summary>The unstructured text to extract signals from. Required.</summary>
    public string? Text { get; set; }

    /// <summary>Optional load number the text is about; annotates signals without inventing data.</summary>
    public string? LoadNumber { get; set; }
}

/// <summary>Read-model of a stored signal for the SPA.</summary>
public sealed record SignalView(
    string Id,
    string SourceType,
    string SourceId,
    SignalType SignalType,
    double Confidence,
    string EvidenceQuote,
    LtlSurface SuggestedSurface,
    string? Summary,
    string? LoadNumber,
    SignalStatus Status,
    string IngestedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt,
    string? DecidedBy);

/// <summary>Ingest response: the signals recorded from one request. Empty list is a valid outcome.</summary>
public sealed record SignalIngestResponse(int Count, IReadOnlyList<SignalView> Signals);

/// <summary>Query filter for listing stored signals.</summary>
public sealed record SignalQuery(
    SignalStatus? Status = null,
    string? SourceType = null,
    string? LoadNumber = null,
    int Max = 100);

/// <summary>
/// Thrown when ingestion must fail closed: the extractor errored/was unavailable, or produced a
/// signal without a verbatim evidence quote. The controller maps this to a legible error and
/// nothing is persisted — no partial writes, no silent drops.
/// </summary>
public sealed class SignalIngestException(string message) : Exception(message);

/// <summary>Shared serialization/mapping helpers so the store, service and tests stay consistent.</summary>
public static class SignalMapping
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static SignalView ToView(SignalRecord r) => new(
        r.Id,
        r.SourceType,
        r.SourceId,
        Enum.Parse<SignalType>(r.SignalType),
        r.Confidence,
        r.EvidenceQuote,
        Enum.Parse<LtlSurface>(r.SuggestedSurface),
        r.Summary,
        r.LoadNumber,
        Enum.Parse<SignalStatus>(r.Status),
        r.IngestedBy,
        r.CreatedAt,
        r.DecidedAt,
        r.DecidedBy);
}
