namespace LtlTool.Api.Features.Ltl.Billing;

/// <summary>Draft/final lifecycle of an assembled invoice. Draft is editable; Final is locked.</summary>
public enum InvoiceStatus
{
    Draft,
    Final,
}

/// <summary>
/// The kind of charge on an invoice line. Deterministic categories the dispatcher/accounting user
/// edits directly — never inferred from "AI". Linehaul is the base freight rate; the rest are the
/// common LTL add-ons operations bill for.
/// </summary>
public enum InvoiceChargeType
{
    Linehaul,
    FuelSurcharge,
    Accessorial,
    Detention,
    Other,
}

/// <summary>One editable money line on a load (rate, fuel surcharge, an accessorial, etc.).</summary>
public sealed class InvoiceCharge
{
    public required string Id { get; init; }
    public required InvoiceChargeType Type { get; init; }
    public string? Description { get; init; }
    public required decimal Amount { get; init; }
}

/// <summary>
/// One load on the invoice (the consolidation parent or a sibling), with its charges and the BOL
/// linkage that billing needs. <see cref="BolPresent"/> is the honest presence flag; a sibling with
/// no BOL is surfaced (never coerced to "present") so accounting can chase it before billing.
/// </summary>
public sealed class InvoiceLoadLine
{
    public required string LoadId { get; init; }
    public string? LoadNumber { get; init; }
    public required bool IsParent { get; init; }
    public string? CustomerName { get; init; }

    /// <summary>Alvys load status, surfaced as-is; null when it was not supplied (not defaulted).</summary>
    public string? Status { get; init; }

    /// <summary>Deep link back to the load in Alvys, when known. Never fabricated.</summary>
    public string? AlvysLoadUrl { get; init; }

    /// <summary>True only when a BOL artifact is actually linked to this load.</summary>
    public required bool BolPresent { get; init; }

    /// <summary>Id of the linked BOL packet artifact, when present.</summary>
    public string? BolArtifactId { get; init; }

    /// <summary>Driver-facing loaded miles (Alvys trip). Null when unknown — never guessed.</summary>
    public decimal? LoadedMiles { get; init; }

    /// <summary>Driver trip rate (Alvys trip value). Null when unknown.</summary>
    public decimal? DriverTripRate { get; init; }

    public IReadOnlyList<InvoiceCharge> Charges { get; init; } = [];

    /// <summary>Sum of this load's charge amounts.</summary>
    public decimal LineTotal => Charges.Sum(c => c.Amount);
}

/// <summary>An immutable entry in an invoice's edit history (who did what, when).</summary>
public sealed class InvoiceEditEvent
{
    public required DateTimeOffset At { get; init; }
    public required string By { get; init; }
    public required string Action { get; init; }
    public string? Detail { get; init; }
}

// --------------------------------------------------------------------------------------------------
// Request shapes (SPA → API)
// --------------------------------------------------------------------------------------------------

/// <summary>A charge supplied by the caller when assembling/updating an invoice.</summary>
public sealed class InvoiceChargeInput
{
    public InvoiceChargeType Type { get; set; } = InvoiceChargeType.Other;
    public string? Description { get; set; }
    public decimal Amount { get; set; }
}

/// <summary>One load supplied by the caller when assembling/updating an invoice.</summary>
public sealed class InvoiceLoadInput
{
    public string? LoadId { get; set; }
    public string? LoadNumber { get; set; }
    public bool IsParent { get; set; }
    public string? CustomerName { get; set; }
    public string? Status { get; set; }
    public string? AlvysLoadUrl { get; set; }
    public bool BolPresent { get; set; }
    public string? BolArtifactId { get; set; }
    public decimal? LoadedMiles { get; set; }
    public decimal? DriverTripRate { get; set; }
    public List<InvoiceChargeInput> Charges { get; set; } = [];
}

/// <summary>Assemble a new invoice from a consolidation (parent + siblings).</summary>
public sealed class AssembleInvoiceRequest
{
    public string? InvoiceNumber { get; set; }
    public string? CorridorCode { get; set; }
    public string? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public string? Notes { get; set; }
    public List<InvoiceLoadInput> Loads { get; set; } = [];
}

/// <summary>Update an existing draft invoice: replace its loads/charges and notes.</summary>
public sealed class UpdateInvoiceRequest
{
    public string? Notes { get; set; }
    public List<InvoiceLoadInput> Loads { get; set; } = [];
}

// --------------------------------------------------------------------------------------------------
// Response shapes (API → SPA)
// --------------------------------------------------------------------------------------------------

/// <summary>Full invoice as returned to the SPA, with parsed loads, history, and computed economics.</summary>
public sealed class InvoiceView
{
    public required string Id { get; init; }
    public required string InvoiceNumber { get; init; }
    public required InvoiceStatus Status { get; init; }
    public string? CorridorCode { get; init; }
    public string? CustomerId { get; init; }
    public string? CustomerName { get; init; }
    public string? ParentLoadId { get; init; }
    public string? ParentLoadNumber { get; init; }
    public string? Notes { get; init; }

    public IReadOnlyList<InvoiceLoadLine> Loads { get; init; } = [];
    public IReadOnlyList<InvoiceEditEvent> EditHistory { get; init; } = [];

    /// <summary>Total the customer owes across all lines (sum of line totals).</summary>
    public required decimal InvoiceTotal { get; init; }

    /// <summary>Sum of the loads' customer-billing charge totals — same set as <see cref="InvoiceTotal"/>.</summary>
    public decimal CombinedRevenue { get; init; }

    /// <summary>Sum of the loads' driver trip rates. Numerator of the combined RPM.</summary>
    public decimal? CombinedDriverTripValue { get; init; }

    /// <summary>Parent load's driver loaded miles. Denominator of the combined RPM.</summary>
    public decimal? DriverLoadedMiles { get; init; }

    /// <summary>
    /// Combined driver trip value ÷ parent driver loaded miles — the operator-facing consolidation
    /// RPM. Null unless both inputs are known (never guessed).
    /// </summary>
    public decimal? CombinedRevenuePerMile { get; init; }

    /// <summary>Load numbers that are missing a BOL — the sibling-tracking flag billing acts on.</summary>
    public IReadOnlyList<string> LoadsMissingBol { get; init; } = [];

    public required string CreatedBy { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string UpdatedBy { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? FinalizedAt { get; init; }
    public string? FinalizedBy { get; init; }

    /// <summary>
    /// Alvys writeback posture for this invoice. Stays <c>NotPerformed</c> — the studio records
    /// app-side only and never pushes to Alvys until the production execution gate is enabled.
    /// </summary>
    public required string AlvysWriteback { get; init; }
}

/// <summary>Compact invoice row for the list view.</summary>
public sealed class InvoiceSummary
{
    public required string Id { get; init; }
    public required string InvoiceNumber { get; init; }
    public required InvoiceStatus Status { get; init; }
    public string? CustomerName { get; init; }
    public string? ParentLoadNumber { get; init; }
    public required int LoadCount { get; init; }
    public required int LoadsMissingBolCount { get; init; }
    public required decimal InvoiceTotal { get; init; }
    public decimal? CombinedRevenuePerMile { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required string AlvysWriteback { get; init; }
}
