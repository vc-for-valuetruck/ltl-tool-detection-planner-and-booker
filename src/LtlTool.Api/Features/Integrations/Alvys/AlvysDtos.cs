using System.Text.Json;
using System.Text.Json.Serialization;

namespace LtlTool.Api.Features.Integrations.Alvys;

/// <summary>
/// Generic Alvys paged envelope: <c>{ Page, PageSize, Total, Items[] }</c>.
/// Alvys returns PascalCase; the JSON options are case-insensitive so binding
/// is resilient to casing changes.
/// </summary>
public class AlvysPagedResponse<T>
{
    [JsonPropertyName("Page")]
    public int Page { get; set; }

    [JsonPropertyName("PageSize")]
    public int PageSize { get; set; }

    [JsonPropertyName("Total")]
    public long Total { get; set; }

    [JsonPropertyName("Items")]
    public List<T> Items { get; set; } = [];
}

/// <summary>
/// Inclusive date window used by the Alvys search filters. <see cref="End"/> is
/// optional — Alvys treats an open end as "up to now".
/// </summary>
public sealed class AlvysDateRange
{
    [JsonPropertyName("Start")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? Start { get; set; }

    [JsonPropertyName("End")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? End { get; set; }
}

/// <summary>
/// Request body for <c>POST /api/p/v{version}/loads/search</c>. Page is 0-based.
/// Alvys requires at least one conditional filter
/// (Status/OrderNumbers/LoadNumbers/PONumbers/CustomerId/UpdatedBy); when no
/// status filter is supplied <see cref="Status"/> defaults to the full list so a
/// bare paged sweep is still a valid request.
/// </summary>
public sealed class LoadSearchRequest
{
    /// <summary>Alvys caps a single LoadNumbers filter at this many entries.</summary>
    public const int MaxLoadNumbers = 150;

    [JsonPropertyName("Page")]
    public int Page { get; set; }

    [JsonPropertyName("PageSize")]
    public int PageSize { get; set; } = 100;

    [JsonPropertyName("DateRange")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AlvysDateRange? DateRange { get; set; }

    [JsonPropertyName("Status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Status { get; set; }

    [JsonPropertyName("OrderNumbers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? OrderNumbers { get; set; }

    [JsonPropertyName("LoadNumbers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? LoadNumbers { get; set; }

    [JsonPropertyName("PONumbers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? PONumbers { get; set; }

    [JsonPropertyName("CustomerId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CustomerId { get; set; }

    [JsonPropertyName("UpdatedAtRange")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AlvysDateRange? UpdatedAtRange { get; set; }

    [JsonPropertyName("UpdatedBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpdatedBy { get; set; }

    [JsonPropertyName("IncludeDeleted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IncludeDeleted { get; set; }

    /// <summary>Full set of Alvys load statuses — used when no status filter is supplied.</summary>
    public static readonly List<string> AllStatuses =
    [
        "Admin", "In Review", "Open", "Quoted", "Reserved", "Covered",
        "Dispatched", "In Transit", "Delivered", "TONU", "Released",
        "Released-Carrier Paid", "Carrier Paid", "Trip Completed", "Queued",
        "Invoiced", "Financed", "Completed", "Paid", "Cancelled", "En-Route",
    ];

    /// <summary>
    /// True when the request already carries at least one of the conditional filters
    /// Alvys requires (Status/OrderNumbers/LoadNumbers/PONumbers/CustomerId/UpdatedBy).
    /// A request with none is rejected server-side (returns 0), so a bare paged sweep
    /// must have <see cref="Status"/> defaulted before it is sent.
    /// </summary>
    public bool HasConditionalFilter =>
        Status is { Count: > 0 }
        || OrderNumbers is { Count: > 0 }
        || LoadNumbers is { Count: > 0 }
        || PONumbers is { Count: > 0 }
        || !string.IsNullOrWhiteSpace(CustomerId)
        || !string.IsNullOrWhiteSpace(UpdatedBy);

    /// <summary>
    /// Ensures the request satisfies Alvys' conditional-filter requirement. When no
    /// conditional filter is set, defaults <see cref="Status"/> to <see cref="AllStatuses"/>
    /// so a bare paged sweep returns loads instead of an empty result. A request that
    /// already carries a filter (e.g. a LoadNumbers lookup) is left untouched.
    /// </summary>
    public void EnsureConditionalFilter()
    {
        if (!HasConditionalFilter)
            Status = AllStatuses;
    }

    /// <summary>
    /// Light client-side guard before the request reaches Alvys. Only the locally
    /// enforceable rules are checked here — <c>PageSize &gt; 0</c> and the
    /// <c>LoadNumbers &lt;= 150</c> cap. Alvys still enforces the conditional-filter
    /// requirement server-side.
    /// </summary>
    public void Validate()
    {
        if (PageSize <= 0)
            throw new ArgumentException("PageSize must be greater than zero.", nameof(PageSize));

        if (LoadNumbers is { Count: > MaxLoadNumbers })
            throw new ArgumentException(
                $"LoadNumbers cannot exceed {MaxLoadNumbers} entries.", nameof(LoadNumbers));
    }
}

/// <summary>Loads search response: paged envelope of <see cref="AlvysLoad"/>.</summary>
public sealed class AlvysLoadsResponse : AlvysPagedResponse<AlvysLoad>;

/// <summary>
/// Pragmatic load projection covering the planner/booker-relevant fields. Unknown
/// JSON properties are tolerated (System.Text.Json ignores them), so this can lag
/// the full Alvys schema without breaking deserialization.
/// </summary>
public sealed class AlvysLoad
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("LoadNumber")]
    public string? LoadNumber { get; set; }

    [JsonPropertyName("OrderNumber")]
    public string? OrderNumber { get; set; }

    [JsonPropertyName("PONumber")]
    public string? PONumber { get; set; }

    [JsonPropertyName("CustomerId")]
    public string? CustomerId { get; set; }

    [JsonPropertyName("CustomerName")]
    public string? CustomerName { get; set; }

    [JsonPropertyName("CustomerNumber")]
    public string? CustomerNumber { get; set; }

    [JsonPropertyName("Status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("TenderId")]
    public string? TenderId { get; set; }

    [JsonPropertyName("ContractId")]
    public string? ContractId { get; set; }

    [JsonPropertyName("CustomerType")]
    public List<string>? CustomerType { get; set; }

    [JsonPropertyName("Stops")]
    public List<AlvysLoadStop>? Stops { get; set; }

    // Alvys ships Fleet as an object ({"Id":"...","Name":"...","InvoiceNumberPrefix":"..."}),
    // not a string. Deserialize into a lightweight AlvysFleet DTO so the whole loads/search
    // response stops failing with 'JSON value could not be converted to System.String'.
    [JsonPropertyName("Fleet")]
    public AlvysFleet? Fleet { get; set; }

    [JsonPropertyName("InvoiceAs")]
    public string? InvoiceAs { get; set; }

    [JsonPropertyName("OfficeId")]
    public string? OfficeId { get; set; }

    [JsonPropertyName("Linehaul")]
    public decimal? Linehaul { get; set; }

    [JsonPropertyName("FuelSurcharge")]
    public decimal? FuelSurcharge { get; set; }

    [JsonPropertyName("CustomerAccessorials")]
    public decimal? CustomerAccessorials { get; set; }

    [JsonPropertyName("CustomerRate")]
    public decimal? CustomerRate { get; set; }

    [JsonPropertyName("InvoicedAmount")]
    public decimal? InvoicedAmount { get; set; }

    [JsonPropertyName("TotalPaid")]
    public decimal? TotalPaid { get; set; }

    [JsonPropertyName("CustomerMileage")]
    public decimal? CustomerMileage { get; set; }

    [JsonPropertyName("Weight")]
    public decimal? Weight { get; set; }

    [JsonPropertyName("Volume")]
    public decimal? Volume { get; set; }

    [JsonPropertyName("ScheduledPickupAt")]
    public DateTimeOffset? ScheduledPickupAt { get; set; }

    [JsonPropertyName("ScheduledDeliveryAt")]
    public DateTimeOffset? ScheduledDeliveryAt { get; set; }

    [JsonPropertyName("ActualPickupAt")]
    public DateTimeOffset? ActualPickupAt { get; set; }

    [JsonPropertyName("ActualDeliveryAt")]
    public DateTimeOffset? ActualDeliveryAt { get; set; }

    [JsonPropertyName("Notes")]
    public List<AlvysNote>? Notes { get; set; }

    [JsonPropertyName("References")]
    public List<AlvysReference>? References { get; set; }

    [JsonPropertyName("RequiredEquipment")]
    public List<string>? RequiredEquipment { get; set; }

    [JsonPropertyName("LoadType")]
    public string? LoadType { get; set; }

    [JsonPropertyName("CustomerAccessorialsDetails")]
    public List<AlvysAccessorialDetail>? CustomerAccessorialsDetails { get; set; }

    [JsonPropertyName("CustomerRepId")]
    public string? CustomerRepId { get; set; }

    [JsonPropertyName("PlannerId")]
    public string? PlannerId { get; set; }

    [JsonPropertyName("ManagerId")]
    public string? ManagerId { get; set; }

    [JsonPropertyName("CreatedAt")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("CreatedBy")]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("UpdatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("UpdatedBy")]
    public string? UpdatedBy { get; set; }

    [JsonPropertyName("CancelledAt")]
    public DateTimeOffset? CancelledAt { get; set; }

    [JsonPropertyName("PaidAt")]
    public DateTimeOffset? PaidAt { get; set; }

    [JsonPropertyName("CancelledBy")]
    public string? CancelledBy { get; set; }

    [JsonPropertyName("PickedUpAt")]
    public DateTimeOffset? PickedUpAt { get; set; }

    [JsonPropertyName("DeliveredAt")]
    public DateTimeOffset? DeliveredAt { get; set; }

    [JsonPropertyName("InvoicedAt")]
    public DateTimeOffset? InvoicedAt { get; set; }

    [JsonPropertyName("LastInvoiceSentAt")]
    public DateTimeOffset? LastInvoiceSentAt { get; set; }

    [JsonPropertyName("Payments")]
    public List<AlvysLoadPayment>? Payments { get; set; }

    [JsonPropertyName("IsDeleted")]
    public bool? IsDeleted { get; set; }
}

// AlvysFleet DTO is defined further down alongside the equipment models — same shape
// (Id/Name/InvoiceNumberPrefix), reused here for the load-search Fleet field.

/// <summary>
/// A payment applied against a load, as carried on the load-detail response for the
/// stop-1-to-billed lifecycle (invoiced → paid). Kept minimal/tolerant of unknown fields.
/// </summary>
public sealed class AlvysLoadPayment
{
    [JsonPropertyName("Amount")]
    public decimal? Amount { get; set; }

    [JsonPropertyName("PaidAt")]
    public DateTimeOffset? PaidAt { get; set; }

    [JsonPropertyName("Reference")]
    public string? Reference { get; set; }

    [JsonPropertyName("Method")]
    public string? Method { get; set; }
}

/// <summary>
/// Query parameters for the read-only load-detail lookup
/// (<c>GET /api/p/v{version}/loads?id=…|loadNumber=…|orderNumber=…</c>). At least one of
/// the three must be supplied; bound from the internal endpoint query string and passed
/// to <see cref="AlvysApiRoutes.LoadDetail"/>. A 404 upstream can mean no such load or an
/// abandoned creation with no trips — both degrade to <c>null</c>.
/// </summary>
public sealed class LoadLookup
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("loadNumber")]
    public string? LoadNumber { get; set; }

    [JsonPropertyName("orderNumber")]
    public string? OrderNumber { get; set; }

    /// <summary>True when at least one lookup key is supplied (non-blank).</summary>
    [JsonIgnore]
    public bool HasCriteria =>
        !string.IsNullOrWhiteSpace(Id)
        || !string.IsNullOrWhiteSpace(LoadNumber)
        || !string.IsNullOrWhiteSpace(OrderNumber);

    /// <summary>
    /// Guards that at least one of <see cref="Id"/>/<see cref="LoadNumber"/>/
    /// <see cref="OrderNumber"/> is supplied before a request reaches Alvys.
    /// </summary>
    public void Validate()
    {
        if (!HasCriteria)
            throw new ArgumentException(
                "A load lookup requires one of id, loadNumber or orderNumber.", nameof(LoadLookup));
    }
}

/// <summary>Postal address as returned on Alvys load/trip stops.</summary>
public sealed class AlvysAddress
{
    [JsonPropertyName("Street")]
    public string? Street { get; set; }

    [JsonPropertyName("City")]
    public string? City { get; set; }

    [JsonPropertyName("State")]
    public string? State { get; set; }

    [JsonPropertyName("Zip")]
    public string? Zip { get; set; }

    [JsonPropertyName("Country")]
    public string? Country { get; set; }
}

/// <summary>Geographic coordinate pair used on trip stops.</summary>
public sealed class AlvysCoordinates
{
    [JsonPropertyName("Latitude")]
    public double? Latitude { get; set; }

    [JsonPropertyName("Longitude")]
    public double? Longitude { get; set; }
}

/// <summary>A stop on a load: address, scheduling windows and references.</summary>
public sealed class AlvysLoadStop
{
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    [JsonPropertyName("StopType")]
    public string? StopType { get; set; }

    [JsonPropertyName("Sequence")]
    public int? Sequence { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Address")]
    public AlvysAddress? Address { get; set; }

    [JsonPropertyName("ScheduledStart")]
    public DateTimeOffset? ScheduledStart { get; set; }

    [JsonPropertyName("ScheduledEnd")]
    public DateTimeOffset? ScheduledEnd { get; set; }

    [JsonPropertyName("References")]
    public List<AlvysReference>? References { get; set; }
}

/// <summary>A free-text note on a load.</summary>
public sealed class AlvysNote
{
    [JsonPropertyName("Text")]
    public string? Text { get; set; }

    [JsonPropertyName("CreatedAt")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("CreatedBy")]
    public string? CreatedBy { get; set; }
}

/// <summary>
/// A reference (name/value tag) attached to a load, trip, or stop. Shape verified
/// empirically 2026-07-18 via live MCP <c>loads_get_by_id</c> + <c>trips_search</c>
/// against the va336 production tenant — see
/// <c>docs/ALVYS_API_DECISIONS.md</c> “Empirical findings, Finding 1”.
/// <para>
/// Trip references (e.g. the <c>LTL</c> boolean on parent, <c>Main Load Id</c> string
/// on child) live on <c>Trip.References[]</c> per the Reuben 2026-07-17 sync
/// and confirmed by MCP payloads. Everything is transported as strings on the wire
/// (<c>Type</c> is one of <c>""</c> / <c>"Text"</c> / <c>"List"</c>).
/// </para>
/// </summary>
public sealed class AlvysReference
{
    /// <summary>Reference row id (stable across reads).</summary>
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    /// <summary>Reference-type id (optional; present for managed reference types).</summary>
    [JsonPropertyName("ReferenceId")]
    public string? ReferenceId { get; set; }

    /// <summary>Human-readable reference name (e.g. <c>"Method of Payment"</c>, <c>"LTL"</c>).</summary>
    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    /// <summary>Reference value — always a string on the wire, even for booleans.</summary>
    [JsonPropertyName("Value")]
    public string? Value { get; set; }

    /// <summary>Data type of the reference — one of <c>""</c>, <c>"Text"</c>, <c>"List"</c>.</summary>
    [JsonPropertyName("Type")]
    public string? Type { get; set; }

    /// <summary>Access scope. Empirical values: <c>"Public"</c>.</summary>
    [JsonPropertyName("Access")]
    public string? Access { get; set; }

    /// <summary>How the reference was created. Empirical values: <c>"Manual"</c>, <c>"EDI"</c>, <c>"Unknown"</c>.</summary>
    [JsonPropertyName("Origin")]
    public string? Origin { get; set; }
}

/// <summary>
/// A distance measurement as it appears on trip mileage fields
/// (<c>TotalMileage</c>, <c>EmptyMileage</c>, <c>LoadedMileage</c>). Shape verified
/// 2026-07-18 empirically via MCP <c>trips_search</c> against va336. Prior versions
/// of this DTO modeled the field as a plain <c>decimal?</c> — that was incorrect and
/// would fail to deserialize the real wire payload.
/// </summary>
public sealed class AlvysDistanceMeasurement
{
    [JsonPropertyName("Distance")]
    public AlvysDistance? Distance { get; set; }

    /// <summary>Empirical: <c>"Engine"</c> when calculated by the routing engine.</summary>
    [JsonPropertyName("Source")]
    public string? Source { get; set; }

    /// <summary>Empirical: <c>"PCMiler"</c>.</summary>
    [JsonPropertyName("ProfileName")]
    public string? ProfileName { get; set; }

    /// <summary>
    /// Convenience accessor: the numeric value of the distance, or <c>null</c> when
    /// no distance is present. Callers doing RPM math should use this rather than
    /// re-implementing null-safety on every access.
    /// </summary>
    [JsonIgnore]
    public decimal? Value => Distance?.Value;
}

/// <summary>
/// The inner distance component of an <see cref="AlvysDistanceMeasurement"/> —
/// numeric magnitude plus its unit of measure (e.g. <c>"Miles"</c>).
/// </summary>
public sealed class AlvysDistance
{
    [JsonPropertyName("Value")]
    public decimal? Value { get; set; }

    [JsonPropertyName("UnitOfMeasure")]
    public string? UnitOfMeasure { get; set; }
}



/// <summary>
/// A document attached to a load, as returned by the read-only
/// <c>GET /api/p/v{version}/loads/{loadNumber}/documents</c> listing. The id is a
/// lowercase <c>id</c>; the remaining fields are PascalCase per the Alvys docs.
/// <see cref="DownloadUrl"/> is a time-limited link (<see cref="ExpiresAt"/>) and is
/// treated as read-only data — documents are not fetched/downloaded in this slice.
/// Unknown JSON properties are tolerated.
/// </summary>
public sealed class AlvysLoadDocument
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("AttachmentPath")]
    public string? AttachmentPath { get; set; }

    [JsonPropertyName("AttachmentType")]
    public string? AttachmentType { get; set; }

    [JsonPropertyName("AttachmentSize")]
    public long? AttachmentSize { get; set; }

    [JsonPropertyName("UploadedAt")]
    public DateTimeOffset? UploadedAt { get; set; }

    [JsonPropertyName("ParentId")]
    public string? ParentId { get; set; }

    [JsonPropertyName("ParentType")]
    public string? ParentType { get; set; }

    [JsonPropertyName("UploadedBy")]
    public string? UploadedBy { get; set; }

    [JsonPropertyName("DownloadUrl")]
    public string? DownloadUrl { get; set; }

    [JsonPropertyName("ExpiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>
/// A note on a load, as returned by the read-only
/// <c>GET /api/p/v{version}/loads/{loadNumber}/notes</c> listing. Distinct from the
/// inline load <see cref="AlvysNote"/> (Text/CreatedAt/CreatedBy): the dedicated notes
/// endpoint returns a richer shape (<c>Id</c>/<c>Description</c>/<c>NoteType</c>/
/// <c>CreatedAt</c>/<c>CreatedBy</c>/<c>CreatedById</c>). Unknown JSON properties are tolerated.
/// </summary>
public sealed class AlvysLoadNote
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("Description")]
    public string? Description { get; set; }

    [JsonPropertyName("NoteType")]
    public string? NoteType { get; set; }

    [JsonPropertyName("CreatedAt")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("CreatedBy")]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("CreatedById")]
    public string? CreatedById { get; set; }
}

/// <summary>A single customer accessorial line.</summary>
public sealed class AlvysAccessorialDetail
{
    [JsonPropertyName("Type")]
    public string? Type { get; set; }

    [JsonPropertyName("Description")]
    public string? Description { get; set; }

    [JsonPropertyName("Amount")]
    public decimal? Amount { get; set; }
}

/// <summary>
/// Request body for <c>POST /api/p/v{version}/trips/search</c>. Page is 0-based.
/// Alvys requires at least one conditional filter
/// (Status/LoadNumbers/TripNumbers/UpdatedBy) server-side.
/// </summary>
public sealed class TripSearchRequest
{
    [JsonPropertyName("Page")]
    public int Page { get; set; }

    [JsonPropertyName("PageSize")]
    public int PageSize { get; set; } = 100;

    [JsonPropertyName("Status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Status { get; set; }

    [JsonPropertyName("LoadNumbers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? LoadNumbers { get; set; }

    [JsonPropertyName("TripNumbers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? TripNumbers { get; set; }

    [JsonPropertyName("PickupDateRange")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AlvysDateRange? PickupDateRange { get; set; }

    [JsonPropertyName("DeliveryDateRange")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AlvysDateRange? DeliveryDateRange { get; set; }

    [JsonPropertyName("UpdatedAtRange")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AlvysDateRange? UpdatedAtRange { get; set; }

    [JsonPropertyName("UpdatedBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpdatedBy { get; set; }

    [JsonPropertyName("IncludeDeleted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IncludeDeleted { get; set; }

    /// <summary>
    /// Light client-side guard: only <c>PageSize &gt; 0</c> is locally enforceable.
    /// Alvys enforces the conditional-filter requirement server-side.
    /// </summary>
    public void Validate()
    {
        if (PageSize <= 0)
            throw new ArgumentException("PageSize must be greater than zero.", nameof(PageSize));
    }
}

/// <summary>Trips search response: paged envelope of <see cref="AlvysTrip"/>.</summary>
public sealed class AlvysTripsResponse : AlvysPagedResponse<AlvysTrip>;

/// <summary>
/// Pragmatic trip projection covering the movement/payroll-relevant fields used by
/// main/child trip logic, mileage and equipment/pay context. Unknown JSON properties
/// are tolerated so this can lag the full Alvys schema.
/// </summary>
public sealed class AlvysTrip
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("TripNumber")]
    public string? TripNumber { get; set; }

    [JsonPropertyName("Status")]
    public string? Status { get; set; }

    [JsonPropertyName("LoadNumber")]
    public string? LoadNumber { get; set; }

    [JsonPropertyName("OrderNumber")]
    public string? OrderNumber { get; set; }

    [JsonPropertyName("TenderAs")]
    public string? TenderAs { get; set; }

    [JsonPropertyName("TenderAsSubsidiaryType")]
    public string? TenderAsSubsidiaryType { get; set; }

    [JsonPropertyName("RequiredEquipment")]
    public List<string>? RequiredEquipment { get; set; }

    [JsonPropertyName("Stops")]
    public List<AlvysTripStop>? Stops { get; set; }

    /// <summary>
    /// Total trip mileage including empty legs. Shape verified 2026-07-18 empirically —
    /// the wire is not a plain decimal; it's a <see cref="AlvysDistanceMeasurement"/>
    /// nested object with <c>Distance</c>, <c>Source</c>, <c>ProfileName</c>. See
    /// <c>docs/ALVYS_API_DECISIONS.md</c> Finding 2.
    /// </summary>
    [JsonPropertyName("TotalMileage")]
    public AlvysDistanceMeasurement? TotalMileage { get; set; }

    [JsonPropertyName("EmptyMileage")]
    public AlvysDistanceMeasurement? EmptyMileage { get; set; }

    /// <summary>
    /// Driver-facing loaded miles (the <em>dispatch mileage</em> in Alvys UI vocabulary).
    /// This is the field Phase 5 consolidation zeroes on child trips per Reuben 2026-07-17
    /// (transcript at 15:55). Never zero <see cref="AlvysLoad.CustomerMileage"/> — that's
    /// the billing mileage which stays populated for accurate customer invoicing.
    /// </summary>
    [JsonPropertyName("LoadedMileage")]
    public AlvysDistanceMeasurement? LoadedMileage { get; set; }

    /// <summary>
    /// Driver-facing trip value (the rate paid to the driver / carrier). Combined driver
    /// RPM formula per Reuben transcript 33:06 is
    /// <c>TripValue.Amount / LoadedMileage.Distance.Value</c>.
    /// </summary>
    [JsonPropertyName("TripValue")]
    public AlvysMoney? TripValue { get; set; }

    [JsonPropertyName("PickupDate")]
    public DateTimeOffset? PickupDate { get; set; }

    [JsonPropertyName("DeliveryDate")]
    public DateTimeOffset? DeliveryDate { get; set; }

    [JsonPropertyName("PickedUpDate")]
    public DateTimeOffset? PickedUpDate { get; set; }

    [JsonPropertyName("DeliveredDate")]
    public DateTimeOffset? DeliveredDate { get; set; }

    [JsonPropertyName("CarrierAssignedDate")]
    public DateTimeOffset? CarrierAssignedDate { get; set; }

    [JsonPropertyName("ReleasedDate")]
    public DateTimeOffset? ReleasedDate { get; set; }

    [JsonPropertyName("PickedUpAt")]
    public DateTimeOffset? PickedUpAt { get; set; }

    [JsonPropertyName("DeliveredAt")]
    public DateTimeOffset? DeliveredAt { get; set; }

    [JsonPropertyName("CarrierAssignedAt")]
    public DateTimeOffset? CarrierAssignedAt { get; set; }

    [JsonPropertyName("ReleasedAt")]
    public DateTimeOffset? ReleasedAt { get; set; }

    [JsonPropertyName("CarrierPaidAt")]
    public DateTimeOffset? CarrierPaidAt { get; set; }

    [JsonPropertyName("DueDate")]
    public DateTimeOffset? DueDate { get; set; }

    [JsonPropertyName("Truck")]
    public AlvysEquipmentRef? Truck { get; set; }

    [JsonPropertyName("Trailer")]
    public AlvysTrailer? Trailer { get; set; }

    [JsonPropertyName("Driver")]
    public AlvysPartyPay? Driver { get; set; }

    [JsonPropertyName("Driver1")]
    public AlvysPartyPay? Driver1 { get; set; }

    [JsonPropertyName("Driver2")]
    public AlvysPartyPay? Driver2 { get; set; }

    [JsonPropertyName("Carrier")]
    public AlvysPartyPay? Carrier { get; set; }

    [JsonPropertyName("OwnerOperator")]
    public AlvysPartyPay? OwnerOperator { get; set; }

    [JsonPropertyName("DispatcherId")]
    public string? DispatcherId { get; set; }

    [JsonPropertyName("DispatchedBy")]
    public string? DispatchedBy { get; set; }

    [JsonPropertyName("ReleasedBy")]
    public string? ReleasedBy { get; set; }

    [JsonPropertyName("CarrierSalesAgentId")]
    public string? CarrierSalesAgentId { get; set; }

    [JsonPropertyName("CarrierPayOnHold")]
    public bool? CarrierPayOnHold { get; set; }

    [JsonPropertyName("References")]
    public List<AlvysReference>? References { get; set; }

    [JsonPropertyName("UpdatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("UpdatedBy")]
    public string? UpdatedBy { get; set; }

    [JsonPropertyName("IsDeleted")]
    public bool? IsDeleted { get; set; }
}

/// <summary>
/// Query parameters for the read-only trip-detail lookup
/// (<c>GET /api/p/v{version}/trips?id=…|tripNumber=…&amp;includeDeleted=…</c>). At least one
/// of <see cref="Id"/>/<see cref="TripNumber"/> must be supplied; the optional
/// <see cref="IncludeDeleted"/> is only emitted when set. Bound from the internal endpoint
/// query string and passed to <see cref="AlvysApiRoutes.TripDetail"/>.
/// </summary>
public sealed class TripLookup
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("tripNumber")]
    public string? TripNumber { get; set; }

    [JsonPropertyName("includeDeleted")]
    public bool? IncludeDeleted { get; set; }

    /// <summary>True when at least one lookup key is supplied (non-blank).</summary>
    [JsonIgnore]
    public bool HasCriteria =>
        !string.IsNullOrWhiteSpace(Id) || !string.IsNullOrWhiteSpace(TripNumber);

    /// <summary>
    /// Guards that at least one of <see cref="Id"/>/<see cref="TripNumber"/> is supplied
    /// before a request reaches Alvys. <see cref="IncludeDeleted"/> alone is not sufficient.
    /// </summary>
    public void Validate()
    {
        if (!HasCriteria)
            throw new ArgumentException(
                "A trip lookup requires one of id or tripNumber.", nameof(TripLookup));
    }
}

/// <summary>
/// A polymorphic stop on a trip, as returned by the read-only
/// <c>GET /api/p/v{version}/trips/{tripId}/stops</c> listing. Alvys discriminates the stop
/// shape with a <c>$type</c> of <c>appointment</c>, <c>delivery_window</c> or <c>waypoint</c>,
/// preserved here in <see cref="Type"/>. Rather than model three subclasses, the union of
/// fields is flattened into one tolerant projection (only the members relevant to a given
/// <c>$type</c> are populated); unknown JSON properties are tolerated.
/// </summary>
public sealed class AlvysTripStopDetail
{
    /// <summary>The Alvys <c>$type</c> discriminator: appointment/delivery_window/waypoint.</summary>
    [JsonPropertyName("$type")]
    public string? Type { get; set; }

    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    [JsonPropertyName("StopType")]
    public string? StopType { get; set; }

    [JsonPropertyName("Status")]
    public string? Status { get; set; }

    [JsonPropertyName("Address")]
    public AlvysContextAddress? Address { get; set; }

    [JsonPropertyName("Coordinates")]
    public AlvysCoordinates? Coordinates { get; set; }

    [JsonPropertyName("ArrivedAt")]
    public DateTimeOffset? ArrivedAt { get; set; }

    [JsonPropertyName("DepartedAt")]
    public DateTimeOffset? DepartedAt { get; set; }

    [JsonPropertyName("CompanyId")]
    public string? CompanyId { get; set; }

    [JsonPropertyName("CompanyNumber")]
    public string? CompanyNumber { get; set; }

    [JsonPropertyName("CompanyName")]
    public string? CompanyName { get; set; }

    [JsonPropertyName("References")]
    public List<AlvysReference>? References { get; set; }

    // appointment ($type = appointment)
    [JsonPropertyName("AppointmentRequested")]
    public bool? AppointmentRequested { get; set; }

    [JsonPropertyName("AppointmentConfirmed")]
    public bool? AppointmentConfirmed { get; set; }

    [JsonPropertyName("AppointmentDate")]
    public DateTimeOffset? AppointmentDate { get; set; }

    // appointment + delivery_window
    [JsonPropertyName("ScheduleType")]
    public string? ScheduleType { get; set; }

    [JsonPropertyName("LoadingType")]
    public string? LoadingType { get; set; }

    // delivery_window + waypoint
    [JsonPropertyName("StopWindow")]
    public AlvysStopWindow? StopWindow { get; set; }
}

/// <summary>An inclusive arrival window (<c>Begin</c>/<c>End</c>) on a trip stop.</summary>
public sealed class AlvysStopWindow
{
    [JsonPropertyName("Begin")]
    public DateTimeOffset? Begin { get; set; }

    [JsonPropertyName("End")]
    public DateTimeOffset? End { get; set; }
}

/// <summary>A stop on a trip: location, schedule and movement timestamps.</summary>
public sealed class AlvysTripStop
{
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    [JsonPropertyName("StopType")]
    public string? StopType { get; set; }

    [JsonPropertyName("ScheduleType")]
    public string? ScheduleType { get; set; }

    [JsonPropertyName("Status")]
    public string? Status { get; set; }

    [JsonPropertyName("Sequence")]
    public int? Sequence { get; set; }

    [JsonPropertyName("CompanyName")]
    public string? CompanyName { get; set; }

    [JsonPropertyName("Address")]
    public AlvysAddress? Address { get; set; }

    [JsonPropertyName("Coordinates")]
    public AlvysCoordinates? Coordinates { get; set; }

    [JsonPropertyName("Appointment")]
    public DateTimeOffset? Appointment { get; set; }

    [JsonPropertyName("StopWindowStart")]
    public DateTimeOffset? StopWindowStart { get; set; }

    [JsonPropertyName("StopWindowEnd")]
    public DateTimeOffset? StopWindowEnd { get; set; }

    [JsonPropertyName("Loading")]
    public bool? Loading { get; set; }

    [JsonPropertyName("ArrivedDate")]
    public DateTimeOffset? ArrivedDate { get; set; }

    [JsonPropertyName("DepartedDate")]
    public DateTimeOffset? DepartedDate { get; set; }

    [JsonPropertyName("References")]
    public List<AlvysReference>? References { get; set; }
}

/// <summary>Minimal equipment reference (truck) carried on a trip.</summary>
public sealed class AlvysEquipmentRef
{
    [JsonPropertyName("Id")]
    public string? Id { get; set; }
}

/// <summary>Trailer reference plus equipment type/length used for planning.</summary>
public sealed class AlvysTrailer
{
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    [JsonPropertyName("EquipmentType")]
    public string? EquipmentType { get; set; }

    [JsonPropertyName("EquipmentLength")]
    public decimal? EquipmentLength { get; set; }
}

/// <summary>
/// Pay context for a trip party (driver/carrier/owner-operator). Kept flexible —
/// accessorials and e-check entries are tolerant lists so payroll detail can be
/// expanded later without breaking deserialization.
/// </summary>
public sealed class AlvysPartyPay
{
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    /// <summary>
    /// Itemized accessorial line items. Fixed from a prior mismatch: the itemized list is
    /// under the JSON key <c>AccessorialsDetails</c>, not <c>Accessorials</c> — <c>Accessorials</c>
    /// (below) is the aggregate money total, per the Alvys OpenAPI schema
    /// (<c>TripResponseCarrierResponse</c>/<c>TripResponseDriverResponse</c>).
    /// </summary>
    [JsonPropertyName("AccessorialsDetails")]
    public List<AlvysAccessorialDetail>? AccessorialsDetails { get; set; }

    [JsonPropertyName("EChecks")]
    public List<AlvysECheck>? EChecks { get; set; }

    /// <summary>Linehaul amount for this party (carrier-side cost when this is the Carrier party).</summary>
    [JsonPropertyName("Linehaul")]
    public AlvysMoney? Linehaul { get; set; }

    /// <summary>Aggregate accessorials total for this party (distinct from the itemized list above).</summary>
    [JsonPropertyName("Accessorials")]
    public AlvysMoney? Accessorials { get; set; }

    /// <summary>Total amount payable to this party (Linehaul + Accessorials, Alvys-computed).</summary>
    [JsonPropertyName("TotalPayable")]
    public AlvysMoney? TotalPayable { get; set; }

    /// <summary>Carrier-provided invoice number for this trip (Carrier party only).</summary>
    [JsonPropertyName("CarrierInvoiceNumber")]
    public string? CarrierInvoiceNumber { get; set; }
}

/// <summary>An e-check / advance line on a trip party's pay.</summary>
public sealed class AlvysECheck
{
    [JsonPropertyName("Number")]
    public string? Number { get; set; }

    [JsonPropertyName("Amount")]
    public decimal? Amount { get; set; }
}

/// <summary>
/// Request body for <c>POST /api/p/v{version}/trailers/search</c>. Page is 0-based.
/// Alvys requires at least one conditional filter
/// (TrailerNumber/FleetName/VinNumber) server-side when others are empty.
/// </summary>
public sealed class TrailerSearchRequest
{
    [JsonPropertyName("Page")]
    public int Page { get; set; }

    [JsonPropertyName("PageSize")]
    public int PageSize { get; set; } = 100;

    [JsonPropertyName("Status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Status { get; set; }

    [JsonPropertyName("TrailerNumber")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TrailerNumber { get; set; }

    [JsonPropertyName("FleetName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FleetName { get; set; }

    [JsonPropertyName("VinNumber")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VinNumber { get; set; }

    /// <summary>
    /// Light client-side guard: only <c>PageSize &gt; 0</c> is locally enforceable.
    /// Alvys enforces the conditional-filter requirement (TrailerNumber/FleetName/
    /// VinNumber required when the other conditional filters are empty) server-side.
    /// </summary>
    public void Validate()
    {
        if (PageSize <= 0)
            throw new ArgumentException("PageSize must be greater than zero.", nameof(PageSize));
    }
}

/// <summary>Trailers search response: paged envelope of <see cref="AlvysTrailerEquipment"/>.</summary>
public sealed class AlvysTrailersResponse : AlvysPagedResponse<AlvysTrailerEquipment>;

/// <summary>
/// Pragmatic trailer master-data projection covering the capacity/equipment/assignment-
/// readiness fields used by the LTL planner/booker. Unknown JSON properties are tolerated
/// so this can lag the full Alvys schema without breaking deserialization.
/// </summary>
public sealed class AlvysTrailerEquipment
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("TrailerNum")]
    public string? TrailerNum { get; set; }

    [JsonPropertyName("Fleet")]
    public AlvysFleet? Fleet { get; set; }

    [JsonPropertyName("Year")]
    public int? Year { get; set; }

    [JsonPropertyName("Make")]
    public string? Make { get; set; }

    [JsonPropertyName("LicenseNum")]
    public string? LicenseNum { get; set; }

    [JsonPropertyName("LicenseState")]
    public string? LicenseState { get; set; }

    [JsonPropertyName("LicenseCountry")]
    public string? LicenseCountry { get; set; }

    [JsonPropertyName("PlateExpiresAt")]
    public DateTimeOffset? PlateExpiresAt { get; set; }

    [JsonPropertyName("LicenseExpiresAt")]
    public DateTimeOffset? LicenseExpiresAt { get; set; }

    [JsonPropertyName("VinNum")]
    public string? VinNum { get; set; }

    [JsonPropertyName("Status")]
    public string? Status { get; set; }

    [JsonPropertyName("SubsidiaryId")]
    public string? SubsidiaryId { get; set; }

    [JsonPropertyName("EquipmentType")]
    public string? EquipmentType { get; set; }

    [JsonPropertyName("EquipmentSize")]
    public string? EquipmentSize { get; set; }

    [JsonPropertyName("Capacity")]
    public AlvysTrailerCapacity? Capacity { get; set; }

    [JsonPropertyName("InsuranceCompany")]
    public string? InsuranceCompany { get; set; }

    [JsonPropertyName("InsurancePolicyNumber")]
    public string? InsurancePolicyNumber { get; set; }

    [JsonPropertyName("InsuranceExpiresAt")]
    public DateTimeOffset? InsuranceExpiresAt { get; set; }

    [JsonPropertyName("InspectionExpiresAt")]
    public DateTimeOffset? InspectionExpiresAt { get; set; }

    [JsonPropertyName("Notes")]
    public List<AlvysNote>? Notes { get; set; }

    [JsonPropertyName("References")]
    public List<AlvysReference>? References { get; set; }

    [JsonPropertyName("CreatedAt")]
    public DateTimeOffset? CreatedAt { get; set; }
}

/// <summary>Fleet reference carried on equipment master data (trucks/trailers).</summary>
public sealed class AlvysFleet
{
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("InvoiceNumberPrefix")]
    public string? InvoiceNumberPrefix { get; set; }
}

/// <summary>Trailer load capacity used for LTL planning/capacity decisions.</summary>
public sealed class AlvysTrailerCapacity
{
    [JsonPropertyName("Pallets")]
    public decimal? Pallets { get; set; }

    [JsonPropertyName("Weight")]
    public decimal? Weight { get; set; }
}

/// <summary>
/// Request body for <c>POST /api/p/v{version}/trucks/search</c>. Page is 0-based.
/// Alvys requires at least one conditional filter
/// (TruckNumber/FleetName/VinNumber/IsActive/RegisteredName) server-side when others
/// are empty.
/// </summary>
public sealed class TruckSearchRequest
{
    [JsonPropertyName("Page")]
    public int Page { get; set; }

    [JsonPropertyName("PageSize")]
    public int PageSize { get; set; } = 100;

    [JsonPropertyName("Status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Status { get; set; }

    [JsonPropertyName("TruckNumber")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TruckNumber { get; set; }

    [JsonPropertyName("FleetName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FleetName { get; set; }

    [JsonPropertyName("VinNumber")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VinNumber { get; set; }

    [JsonPropertyName("IsActive")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsActive { get; set; }

    [JsonPropertyName("RegisteredName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RegisteredName { get; set; }

    /// <summary>
    /// Light client-side guard: only <c>PageSize &gt; 0</c> is locally enforceable.
    /// Alvys enforces the conditional-filter requirement (TruckNumber/FleetName/
    /// VinNumber/IsActive/RegisteredName required when the other conditional filters
    /// are empty) server-side.
    /// </summary>
    public void Validate()
    {
        if (PageSize <= 0)
            throw new ArgumentException("PageSize must be greater than zero.", nameof(PageSize));
    }
}

/// <summary>Trucks search response: paged envelope of <see cref="AlvysTruck"/>.</summary>
public sealed class AlvysTrucksResponse : AlvysPagedResponse<AlvysTruck>;

/// <summary>
/// Pragmatic truck master-data projection covering the capacity/equipment/assignment-
/// readiness fields used by the LTL planner/booker. Unknown JSON properties are tolerated
/// so this can lag the full Alvys schema without breaking deserialization.
/// </summary>
public sealed class AlvysTruck
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("TruckNum")]
    public string? TruckNum { get; set; }

    [JsonPropertyName("VinNumber")]
    public string? VinNumber { get; set; }

    [JsonPropertyName("Year")]
    public int? Year { get; set; }

    [JsonPropertyName("Make")]
    public string? Make { get; set; }

    [JsonPropertyName("Model")]
    public string? Model { get; set; }

    [JsonPropertyName("LicenseNum")]
    public string? LicenseNum { get; set; }

    [JsonPropertyName("LicenseState")]
    public string? LicenseState { get; set; }

    [JsonPropertyName("LicenseCountry")]
    public string? LicenseCountry { get; set; }

    [JsonPropertyName("PlateExpirationDate")]
    public DateTimeOffset? PlateExpirationDate { get; set; }

    [JsonPropertyName("LicenseExpirationDate")]
    public DateTimeOffset? LicenseExpirationDate { get; set; }

    [JsonPropertyName("Status")]
    public string? Status { get; set; }

    [JsonPropertyName("SubsidiaryId")]
    public string? SubsidiaryId { get; set; }

    [JsonPropertyName("NumberOfAxles")]
    public int? NumberOfAxles { get; set; }

    [JsonPropertyName("Fleet")]
    public AlvysFleet? Fleet { get; set; }

    [JsonPropertyName("GrossWeight")]
    public decimal? GrossWeight { get; set; }

    [JsonPropertyName("EmptyWeight")]
    public decimal? EmptyWeight { get; set; }

    [JsonPropertyName("Color")]
    public string? Color { get; set; }

    [JsonPropertyName("FuelType")]
    public string? FuelType { get; set; }

    [JsonPropertyName("FuelCards")]
    public List<AlvysFuelCard>? FuelCards { get; set; }

    [JsonPropertyName("InsuranceCompany")]
    public string? InsuranceCompany { get; set; }

    [JsonPropertyName("InsurancePolicyNumber")]
    public string? InsurancePolicyNumber { get; set; }

    [JsonPropertyName("InsuranceExpirationDate")]
    public DateTimeOffset? InsuranceExpirationDate { get; set; }

    [JsonPropertyName("InspectionExpirationDate")]
    public DateTimeOffset? InspectionExpirationDate { get; set; }

    [JsonPropertyName("Notes")]
    public List<AlvysNote>? Notes { get; set; }

    [JsonPropertyName("References")]
    public List<AlvysReference>? References { get; set; }

    [JsonPropertyName("CreatedAt")]
    public DateTimeOffset? CreatedAt { get; set; }
}

/// <summary>A fuel card assigned to a truck. Kept minimal/tolerant of unknown fields.</summary>
public sealed class AlvysFuelCard
{
    [JsonPropertyName("Number")]
    public string? Number { get; set; }

    [JsonPropertyName("Type")]
    public string? Type { get; set; }
}

/// <summary>
/// Postal address as returned on Alvys context resources (locations/drivers/customers).
/// Distinct from <see cref="AlvysAddress"/>: the public search API returns
/// <c>ZipCode</c> (not <c>Zip</c>) and no country on these resources.
/// </summary>
public sealed class AlvysContextAddress
{
    [JsonPropertyName("Street")]
    public string? Street { get; set; }

    [JsonPropertyName("City")]
    public string? City { get; set; }

    [JsonPropertyName("State")]
    public string? State { get; set; }

    [JsonPropertyName("ZipCode")]
    public string? ZipCode { get; set; }
}

/// <summary>
/// A note on an Alvys context resource (location/driver/customer). The public search
/// API uses a richer note shape than load <see cref="AlvysNote"/>: a lowercase
/// <c>id</c>, plus <c>Description</c>/<c>NoteType</c>/<c>Time</c>/<c>User</c>/<c>UserId</c>.
/// </summary>
public sealed class AlvysContextNote
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("Description")]
    public string? Description { get; set; }

    [JsonPropertyName("NoteType")]
    public string? NoteType { get; set; }

    [JsonPropertyName("Time")]
    public DateTimeOffset? Time { get; set; }

    [JsonPropertyName("User")]
    public string? User { get; set; }

    [JsonPropertyName("UserId")]
    public string? UserId { get; set; }
}

/// <summary>
/// Request body for <c>POST /api/p/v{version}/dispatchpreferences/search</c>. All filters
/// are optional; the response is a bare array (not a paged envelope). Provides the
/// dispatcher/driver/truck/trailer assignment context used by the LTL planner/booker.
/// </summary>
public sealed class DispatchPreferenceSearchRequest
{
    [JsonPropertyName("DispatcherIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? DispatcherIds { get; set; }

    [JsonPropertyName("DriverIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? DriverIds { get; set; }

    [JsonPropertyName("TruckIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? TruckIds { get; set; }

    [JsonPropertyName("TrailerIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? TrailerIds { get; set; }

    [JsonPropertyName("UpdatedAtStart")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? UpdatedAtStart { get; set; }

    [JsonPropertyName("UpdatedAtEnd")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? UpdatedAtEnd { get; set; }
}

/// <summary>
/// A dispatch preference: the dispatcher/driver/truck/trailer assignment pairing as of
/// <see cref="UpdatedAt"/>. Unknown JSON properties are tolerated.
/// </summary>
public sealed class AlvysDispatchPreference
{
    [JsonPropertyName("UpdatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("DispatcherId")]
    public string? DispatcherId { get; set; }

    [JsonPropertyName("Driver1Id")]
    public string? Driver1Id { get; set; }

    [JsonPropertyName("Driver2Id")]
    public string? Driver2Id { get; set; }

    [JsonPropertyName("TruckId")]
    public string? TruckId { get; set; }

    [JsonPropertyName("TrailerId")]
    public string? TrailerId { get; set; }
}

/// <summary>
/// Request body for <c>POST /api/p/v{version}/locations/search</c>. Page is 0-based.
/// Provides pickup/delivery/hub/yard geography and shipper/consignee/warehouse context.
/// </summary>
public sealed class LocationSearchRequest
{
    [JsonPropertyName("Page")]
    public int Page { get; set; }

    [JsonPropertyName("PageSize")]
    public int PageSize { get; set; } = 100;

    [JsonPropertyName("Status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Status { get; set; }

    [JsonPropertyName("LocationIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? LocationIds { get; set; }

    [JsonPropertyName("CreatedDateRange")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AlvysDateRange? CreatedDateRange { get; set; }

    /// <summary>Light client-side guard: only <c>PageSize &gt; 0</c> is locally enforceable.</summary>
    public void Validate()
    {
        if (PageSize <= 0)
            throw new ArgumentException("PageSize must be greater than zero.", nameof(PageSize));
    }
}

/// <summary>Locations search response: paged envelope of <see cref="AlvysLocation"/>.</summary>
public sealed class AlvysLocationsResponse : AlvysPagedResponse<AlvysLocation>;

/// <summary>
/// Pragmatic location projection covering the geography/contact fields used by the LTL
/// planner/booker. Unknown JSON properties (including <c>Facets</c>/<c>Aggregations</c>
/// on the envelope) are tolerated.
/// </summary>
public sealed class AlvysLocation
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("CompanyNumber")]
    public string? CompanyNumber { get; set; }

    [JsonPropertyName("Type")]
    public string? Type { get; set; }

    [JsonPropertyName("Status")]
    public string? Status { get; set; }

    [JsonPropertyName("PhysicalAddress")]
    public AlvysContextAddress? PhysicalAddress { get; set; }

    [JsonPropertyName("Email")]
    public List<string>? Email { get; set; }

    [JsonPropertyName("Phone")]
    public List<string>? Phone { get; set; }

    [JsonPropertyName("Fax")]
    public string? Fax { get; set; }

    [JsonPropertyName("DateCreated")]
    public DateTimeOffset? DateCreated { get; set; }

    [JsonPropertyName("ExternalId")]
    public string? ExternalId { get; set; }

    [JsonPropertyName("Notes")]
    public List<AlvysContextNote>? Notes { get; set; }
}

/// <summary>
/// Request body for <c>POST /api/p/v{version}/drivers/search</c>. Page is 0-based.
/// Provides driver assignment/readiness context. Alvys requires at least one conditional
/// filter (Status/Name/EmployeeId/FleetName/IsActive) server-side when others are empty.
/// </summary>
public sealed class DriverSearchRequest
{
    [JsonPropertyName("Page")]
    public int Page { get; set; }

    [JsonPropertyName("PageSize")]
    public int PageSize { get; set; } = 100;

    [JsonPropertyName("Status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Status { get; set; }

    [JsonPropertyName("Name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("EmployeeId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EmployeeId { get; set; }

    [JsonPropertyName("FleetName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FleetName { get; set; }

    [JsonPropertyName("IsActive")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsActive { get; set; }

    /// <summary>Light client-side guard: only <c>PageSize &gt; 0</c> is locally enforceable.</summary>
    public void Validate()
    {
        if (PageSize <= 0)
            throw new ArgumentException("PageSize must be greater than zero.", nameof(PageSize));
    }
}

/// <summary>Drivers search response: paged envelope of <see cref="AlvysDriver"/>.</summary>
public sealed class AlvysDriversResponse : AlvysPagedResponse<AlvysDriver>;

/// <summary>
/// Pragmatic driver projection covering identity/contact/assignment-readiness fields used
/// by the LTL planner/booker (license/medical expiries, hire/terminate, fleet). Unknown
/// JSON properties are tolerated.
/// </summary>
public sealed class AlvysDriver
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("EmployeeId")]
    public string? EmployeeId { get; set; }

    [JsonPropertyName("PhoneNumber")]
    public string? PhoneNumber { get; set; }

    [JsonPropertyName("UserId")]
    public string? UserId { get; set; }

    [JsonPropertyName("Email")]
    public string? Email { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Type")]
    public string? Type { get; set; }

    [JsonPropertyName("SubsidiaryId")]
    public string? SubsidiaryId { get; set; }

    [JsonPropertyName("Address")]
    public AlvysContextAddress? Address { get; set; }

    [JsonPropertyName("Status")]
    public string? Status { get; set; }

    [JsonPropertyName("IsActive")]
    public bool? IsActive { get; set; }

    [JsonPropertyName("LicenseNum")]
    public string? LicenseNum { get; set; }

    [JsonPropertyName("LicenseState")]
    public string? LicenseState { get; set; }

    [JsonPropertyName("LicenseCountry")]
    public string? LicenseCountry { get; set; }

    [JsonPropertyName("LicenseExpiresAt")]
    public DateTimeOffset? LicenseExpiresAt { get; set; }

    [JsonPropertyName("MedicalExpiresAt")]
    public DateTimeOffset? MedicalExpiresAt { get; set; }

    [JsonPropertyName("HiredAt")]
    public DateTimeOffset? HiredAt { get; set; }

    [JsonPropertyName("TerminatedAt")]
    public DateTimeOffset? TerminatedAt { get; set; }

    [JsonPropertyName("Notes")]
    public List<AlvysContextNote>? Notes { get; set; }

    [JsonPropertyName("Fleet")]
    public AlvysFleet? Fleet { get; set; }

    [JsonPropertyName("References")]
    public List<AlvysDriverReference>? References { get; set; }

    [JsonPropertyName("CreatedAt")]
    public DateTimeOffset? CreatedAt { get; set; }
}

/// <summary>A typed reference on a driver record. Kept tolerant of unknown fields.</summary>
public sealed class AlvysDriverReference
{
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    [JsonPropertyName("ReferenceId")]
    public string? ReferenceId { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Value")]
    public string? Value { get; set; }

    [JsonPropertyName("Type")]
    public string? Type { get; set; }

    [JsonPropertyName("Access")]
    public string? Access { get; set; }

    [JsonPropertyName("Origin")]
    public string? Origin { get; set; }
}

/// <summary>
/// Request body for <c>POST /api/p/v{version}/customers/search</c>. Page is 0-based.
/// <see cref="Statuses"/> is required by Alvys. Provides billing separation, customer
/// policy/approval and customer-specific matching context.
/// </summary>
public sealed class CustomerSearchRequest
{
    [JsonPropertyName("Page")]
    public int Page { get; set; }

    [JsonPropertyName("PageSize")]
    public int PageSize { get; set; } = 100;

    [JsonPropertyName("Statuses")]
    public List<string> Statuses { get; set; } = [];

    [JsonPropertyName("CreatedDateRange")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AlvysDateRange? CreatedDateRange { get; set; }

    /// <summary>Light client-side guard: only <c>PageSize &gt; 0</c> is locally enforceable.</summary>
    public void Validate()
    {
        if (PageSize <= 0)
            throw new ArgumentException("PageSize must be greater than zero.", nameof(PageSize));
    }
}

/// <summary>Customers search response: paged envelope of <see cref="AlvysCustomer"/>.</summary>
public sealed class AlvysCustomersResponse : AlvysPagedResponse<AlvysCustomer>;

/// <summary>
/// Pragmatic customer projection covering billing/invoicing/contact/policy fields used by
/// the LTL planner/booker for billing separation and customer-specific matching. Unknown
/// JSON properties are tolerated.
/// </summary>
public sealed class AlvysCustomer
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("CompanyNumber")]
    public string? CompanyNumber { get; set; }

    [JsonPropertyName("Type")]
    public string? Type { get; set; }

    [JsonPropertyName("Status")]
    public string? Status { get; set; }

    [JsonPropertyName("BillingAddress")]
    public AlvysContextAddress? BillingAddress { get; set; }

    [JsonPropertyName("Email")]
    public List<string>? Email { get; set; }

    [JsonPropertyName("Phone")]
    public List<string>? Phone { get; set; }

    [JsonPropertyName("Fax")]
    public string? Fax { get; set; }

    [JsonPropertyName("DateCreated")]
    public DateTimeOffset? DateCreated { get; set; }

    [JsonPropertyName("InvoicingInformation")]
    public AlvysInvoicingInformation? InvoicingInformation { get; set; }

    [JsonPropertyName("ExternalId")]
    public string? ExternalId { get; set; }

    [JsonPropertyName("Contacts")]
    public List<AlvysCustomerContact>? Contacts { get; set; }

    [JsonPropertyName("SalesAgentId")]
    public string? SalesAgentId { get; set; }

    [JsonPropertyName("Notes")]
    public List<AlvysContextNote>? Notes { get; set; }
}

/// <summary>Customer invoicing/billing terms used for billing separation decisions.</summary>
public sealed class AlvysInvoicingInformation
{
    [JsonPropertyName("Address")]
    public AlvysContextAddress? Address { get; set; }

    [JsonPropertyName("EmailAddresses")]
    public List<string>? EmailAddresses { get; set; }

    [JsonPropertyName("PhoneNumber")]
    public string? PhoneNumber { get; set; }

    [JsonPropertyName("InvoicingName")]
    public string? InvoicingName { get; set; }

    [JsonPropertyName("InvoicingNameAlias")]
    public string? InvoicingNameAlias { get; set; }

    [JsonPropertyName("PaymentType")]
    public string? PaymentType { get; set; }

    [JsonPropertyName("PaymentTermsInDays")]
    public int? PaymentTermsInDays { get; set; }
}

/// <summary>A contact person on a customer record.</summary>
public sealed class AlvysCustomerContact
{
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Email")]
    public string? Email { get; set; }

    [JsonPropertyName("Phone")]
    public string? Phone { get; set; }

    [JsonPropertyName("Mobile")]
    public string? Mobile { get; set; }

    [JsonPropertyName("Extension")]
    public string? Extension { get; set; }

    [JsonPropertyName("Title")]
    public string? Title { get; set; }
}

/// <summary>
/// Request body for <c>POST /api/p/v{version}/users/search</c>. Page is 0-based.
/// Provides dispatcher display names/roles/filters.
/// </summary>
public sealed class UserSearchRequest
{
    [JsonPropertyName("Page")]
    public int Page { get; set; }

    [JsonPropertyName("PageSize")]
    public int PageSize { get; set; } = 100;

    [JsonPropertyName("Keyword")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Keyword { get; set; }

    /// <summary>Light client-side guard: only <c>PageSize &gt; 0</c> is locally enforceable.</summary>
    public void Validate()
    {
        if (PageSize <= 0)
            throw new ArgumentException("PageSize must be greater than zero.", nameof(PageSize));
    }
}

/// <summary>Users search response: paged envelope of <see cref="AlvysUser"/>.</summary>
public sealed class AlvysUsersResponse : AlvysPagedResponse<AlvysUser>;

/// <summary>
/// Pragmatic user projection covering dispatcher display name/role/permissions used by the
/// LTL planner/booker. <c>Role</c>/<c>Status</c>/<c>UserType</c> are kept as strings (not
/// enums) for tolerance, consistent with the other Alvys read models. Unknown JSON
/// properties are tolerated.
/// </summary>
public sealed class AlvysUser
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("UserName")]
    public string? UserName { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Email")]
    public string? Email { get; set; }

    [JsonPropertyName("UserType")]
    public string? UserType { get; set; }

    [JsonPropertyName("Role")]
    public string? Role { get; set; }

    [JsonPropertyName("Phone")]
    public string? Phone { get; set; }

    [JsonPropertyName("CompanyCode")]
    public string? CompanyCode { get; set; }

    [JsonPropertyName("Status")]
    public string? Status { get; set; }

    [JsonPropertyName("Permissions")]
    public List<string>? Permissions { get; set; }

    [JsonPropertyName("CreatedAt")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("ModifiedAt")]
    public DateTimeOffset? ModifiedAt { get; set; }
}

/// <summary>
/// Request body for <c>POST /api/p/v{version}/tenders/search</c>. Page is 0-based.
/// <see cref="Page"/> and <see cref="PageSize"/> are required by Alvys; <see cref="Sort"/>
/// and <see cref="Filter"/> are optional. Provides inbound EDI/tender offers as a planning
/// source for the LTL detection/planner/booker.
/// </summary>
public sealed class TenderSearchRequest
{
    [JsonPropertyName("Page")]
    public int Page { get; set; }

    [JsonPropertyName("PageSize")]
    public int PageSize { get; set; } = 100;

    [JsonPropertyName("Sort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TenderSort? Sort { get; set; }

    [JsonPropertyName("Filter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TenderSearchFilter? Filter { get; set; }

    /// <summary>Light client-side guard: only <c>PageSize &gt; 0</c> is locally enforceable.</summary>
    public void Validate()
    {
        if (PageSize <= 0)
            throw new ArgumentException("PageSize must be greater than zero.", nameof(PageSize));
    }
}

/// <summary>Sort directive on a tender search: a field name and a direction.</summary>
public sealed class TenderSort
{
    [JsonPropertyName("Field")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Field { get; set; }

    [JsonPropertyName("Direction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Direction { get; set; }
}

/// <summary>Optional filter block on a tender search. All members are optional.</summary>
public sealed class TenderSearchFilter
{
    [JsonPropertyName("Status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Status { get; set; }

    [JsonPropertyName("CreatedAtRange")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AlvysDateRange? CreatedAtRange { get; set; }

    [JsonPropertyName("Type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    [JsonPropertyName("Source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    [JsonPropertyName("SourceCustomer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceCustomer { get; set; }

    [JsonPropertyName("ShipmentId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShipmentId { get; set; }

    [JsonPropertyName("LoadNumber")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LoadNumber { get; set; }

    [JsonPropertyName("ExternalTenderId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExternalTenderId { get; set; }
}

/// <summary>Tenders search response: paged envelope of <see cref="AlvysTender"/>.</summary>
public sealed class AlvysTendersResponse : AlvysPagedResponse<AlvysTender>;

/// <summary>
/// Pragmatic tender projection covering the inbound-offer fields used by the LTL
/// planner/booker. Alvys casing is preserved and unknown JSON properties (including
/// envelope <c>Facets</c>/<c>Aggregations</c>) are tolerated, so this can lag the full
/// Alvys schema without breaking deserialization.
/// </summary>
public sealed class AlvysTender
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("CompanyCode")]
    public string? CompanyCode { get; set; }

    [JsonPropertyName("Status")]
    public string? Status { get; set; }

    [JsonPropertyName("DateImported")]
    public AlvysTenderDateTime? DateImported { get; set; }

    [JsonPropertyName("ShipmentId")]
    public string? ShipmentId { get; set; }

    [JsonPropertyName("LoadNumber")]
    public string? LoadNumber { get; set; }

    [JsonPropertyName("Equipment")]
    public AlvysTenderEquipment? Equipment { get; set; }

    [JsonPropertyName("Entities")]
    public List<AlvysTenderEntity>? Entities { get; set; }

    [JsonPropertyName("PaymentMethod")]
    public string? PaymentMethod { get; set; }

    [JsonPropertyName("QtyPallets")]
    public int? QtyPallets { get; set; }

    [JsonPropertyName("SCAC")]
    public string? SCAC { get; set; }

    [JsonPropertyName("Weight")]
    public decimal? Weight { get; set; }

    [JsonPropertyName("WeightUnitCode")]
    public string? WeightUnitCode { get; set; }

    [JsonPropertyName("Volume")]
    public decimal? Volume { get; set; }

    [JsonPropertyName("VolumeUnitCode")]
    public string? VolumeUnitCode { get; set; }

    [JsonPropertyName("Rate")]
    public decimal? Rate { get; set; }

    [JsonPropertyName("ExpirationDate")]
    public AlvysTenderDateTime? ExpirationDate { get; set; }

    [JsonPropertyName("Notes")]
    public List<string>? Notes { get; set; }

    [JsonPropertyName("Stops")]
    public List<AlvysTenderStop>? Stops { get; set; }

    [JsonPropertyName("References")]
    public List<AlvysTenderReference>? References { get; set; }

    [JsonPropertyName("RoutingSequenceCode")]
    public string? RoutingSequenceCode { get; set; }

    [JsonPropertyName("TransportationMethodTypeCode")]
    public string? TransportationMethodTypeCode { get; set; }

    [JsonPropertyName("Etag")]
    public string? Etag { get; set; }
}

/// <summary>Equipment requested on a tender (number/length/type).</summary>
public sealed class AlvysTenderEquipment
{
    [JsonPropertyName("Number")]
    public string? Number { get; set; }

    [JsonPropertyName("Length")]
    public decimal? Length { get; set; }

    [JsonPropertyName("Type")]
    public string? Type { get; set; }
}

/// <summary>
/// An EDI party/entity on a tender or tender stop (shipper/consignee/bill-to, etc.).
/// Carries the N1/N3/N4-style identity and address fields from the inbound tender.
/// </summary>
public sealed class AlvysTenderEntity
{
    [JsonPropertyName("Type")]
    public string? Type { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("CompanyName")]
    public string? CompanyName { get; set; }

    [JsonPropertyName("IdCodeQualifier")]
    public string? IdCodeQualifier { get; set; }

    [JsonPropertyName("IdCode")]
    public string? IdCode { get; set; }

    [JsonPropertyName("N1Qualifier")]
    public string? N1Qualifier { get; set; }

    [JsonPropertyName("Street")]
    public string? Street { get; set; }

    [JsonPropertyName("City")]
    public string? City { get; set; }

    [JsonPropertyName("PostalCode")]
    public string? PostalCode { get; set; }

    [JsonPropertyName("CountryCode")]
    public string? CountryCode { get; set; }

    [JsonPropertyName("Phone")]
    public string? Phone { get; set; }

    [JsonPropertyName("Email")]
    public string? Email { get; set; }

    [JsonPropertyName("State")]
    public string? State { get; set; }
}

/// <summary>
/// A tender date-time: the instant plus an optional IANA/Olson <c>TimeZoneCode</c>. Alvys
/// returns tender schedule/expiry timestamps in this wrapper rather than a bare instant.
/// </summary>
public sealed class AlvysTenderDateTime
{
    [JsonPropertyName("DateTime")]
    public DateTimeOffset DateTime { get; set; }

    [JsonPropertyName("TimeZoneCode")]
    public string? TimeZoneCode { get; set; }
}

/// <summary>A stop on a tender: entity, schedule windows, order detail and references.</summary>
public sealed class AlvysTenderStop
{
    [JsonPropertyName("StopId")]
    public string StopId { get; set; } = string.Empty;

    [JsonPropertyName("Type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("Entity")]
    public AlvysTenderEntity? Entity { get; set; }

    [JsonPropertyName("SequenceNumber")]
    public int? SequenceNumber { get; set; }

    [JsonPropertyName("Orders")]
    public List<AlvysTenderOrderDetail>? Orders { get; set; }

    [JsonPropertyName("References")]
    public List<AlvysTenderReference>? References { get; set; }

    [JsonPropertyName("WeightQualifier")]
    public string? WeightQualifier { get; set; }

    [JsonPropertyName("ArrivedAt")]
    public AlvysTenderDateTime? ArrivedAt { get; set; }

    [JsonPropertyName("DepartedAt")]
    public AlvysTenderDateTime? DepartedAt { get; set; }

    [JsonPropertyName("ScheduledArrivalStart")]
    public AlvysTenderDateTime? ScheduledArrivalStart { get; set; }

    [JsonPropertyName("ScheduledArrivalEnd")]
    public AlvysTenderDateTime? ScheduledArrivalEnd { get; set; }

    [JsonPropertyName("StopReasonCode")]
    public string? StopReasonCode { get; set; }

    [JsonPropertyName("Notes")]
    public List<string>? Notes { get; set; }
}

/// <summary>A line of order detail on a tender stop (quantity/weight/volume + references).</summary>
public sealed class AlvysTenderOrderDetail
{
    [JsonPropertyName("Quantity")]
    public decimal? Quantity { get; set; }

    [JsonPropertyName("WeightUnitCode")]
    public string? WeightUnitCode { get; set; }

    [JsonPropertyName("Weight")]
    public decimal? Weight { get; set; }

    [JsonPropertyName("ReferenceId")]
    public string? ReferenceId { get; set; }

    [JsonPropertyName("PoNumber")]
    public string? PoNumber { get; set; }

    [JsonPropertyName("VolumeUnitQualifier")]
    public string? VolumeUnitQualifier { get; set; }

    [JsonPropertyName("Volume")]
    public decimal? Volume { get; set; }

    [JsonPropertyName("UnitBasisForMeasurement")]
    public string? UnitBasisForMeasurement { get; set; }

    [JsonPropertyName("Description")]
    public string? Description { get; set; }

    [JsonPropertyName("ReferenceId2")]
    public string? ReferenceId2 { get; set; }

    [JsonPropertyName("SequenceNumber")]
    public int? SequenceNumber { get; set; }
}

/// <summary>
/// A typed reference on a tender or tender stop. Distinct from the load
/// <see cref="AlvysReference"/> (Type/Value): the tender reference uses
/// <c>Id</c>/<c>Qualifier</c>/<c>Description</c>.
/// </summary>
public sealed class AlvysTenderReference
{
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    [JsonPropertyName("Qualifier")]
    public string? Qualifier { get; set; }

    [JsonPropertyName("Description")]
    public string? Description { get; set; }
}

// ---------------------------------------------------------------------------
// Invoices (read-only): search + single-invoice detail.
// ---------------------------------------------------------------------------

/// <summary>
/// Request body for <c>POST /api/p/v{version}/invoices/search</c>. Page is 0-based.
/// All filters are conditional and omitted from the serialized body when null so only
/// the supplied criteria reach Alvys. <see cref="Validate"/> enforces only the locally
/// checkable rule (<c>PageSize &gt; 0</c>); Alvys still enforces its own filter rules
/// server-side.
/// </summary>
public sealed class InvoiceSearchRequest
{
    [JsonPropertyName("Page")]
    public int Page { get; set; }

    [JsonPropertyName("PageSize")]
    public int PageSize { get; set; } = 100;

    [JsonPropertyName("InvoicedDateRange")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AlvysDateRange? InvoicedDateRange { get; set; }

    [JsonPropertyName("InvoiceSentRange")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AlvysDateRange? InvoiceSentRange { get; set; }

    [JsonPropertyName("PaidDateRange")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AlvysDateRange? PaidDateRange { get; set; }

    [JsonPropertyName("Status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Status { get; set; }

    [JsonPropertyName("LoadNumbers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? LoadNumbers { get; set; }

    [JsonPropertyName("PONumbers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? PONumbers { get; set; }

    [JsonPropertyName("OrderNumbers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? OrderNumbers { get; set; }

    [JsonPropertyName("CustomerId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CustomerId { get; set; }

    /// <summary>Light client-side guard: only <c>PageSize &gt; 0</c> is enforceable here.</summary>
    public void Validate()
    {
        if (PageSize <= 0)
            throw new ArgumentException("PageSize must be greater than zero.", nameof(PageSize));
    }
}

/// <summary>Invoices search response: paged envelope of <see cref="AlvysInvoice"/>.</summary>
public sealed class AlvysInvoicesResponse : AlvysPagedResponse<AlvysInvoice>;

/// <summary>
/// A monetary amount as carried on Alvys money-valued fields: an amount plus a currency.
/// Both are nullable/tolerant of missing values.
/// <para>
/// <b>Wire-format quirk (verified 2026-07-18 via MCP).</b> Different Alvys endpoints carry
/// the currency in different shapes. Invoice endpoints (Linehaul / Accessorials /
/// TotalPayable) return <c>"Currency": "USD"</c> (ISO-4217 alpha). Trip endpoints
/// (<see cref="AlvysTrip.TripValue"/>) return <c>"Currency": 840</c> (ISO-4217 numeric).
/// This DTO stores whichever came in as a normalised alpha code string via the
/// <see cref="AlvysMoneyCurrencyConverter"/>. Numeric codes are translated on read using
/// the ISO-4217 alpha map below; unknown numeric codes fall through as their string form
/// (e.g. <c>"840"</c>) so nothing silently drops.
/// </para>
/// </summary>
public sealed class AlvysMoney
{
    [JsonPropertyName("Amount")]
    public decimal? Amount { get; set; }

    [JsonPropertyName("Currency")]
    [JsonConverter(typeof(AlvysMoneyCurrencyConverter))]
    public string? Currency { get; set; }
}

/// <summary>
/// Reads the <see cref="AlvysMoney.Currency"/> field in either the invoice shape
/// (<c>"USD"</c> string) or the trip shape (<c>840</c> numeric ISO-4217 code). Writes
/// always emit a string. Kept in this file so the DTO+converter pair is co-located.
/// </summary>
internal sealed class AlvysMoneyCurrencyConverter : JsonConverter<string?>
{
    // ISO-4217 numeric → alpha for the currencies we're likely to see in Value Truck's
    // freight lanes. Extend when a new currency shows up empirically — do not preemptively
    // add currencies to keep the map honest.
    private static readonly Dictionary<int, string> NumericToAlpha = new()
    {
        { 840, "USD" },  // United States dollar
        { 124, "CAD" },  // Canadian dollar (US↔CA freight)
        { 484, "MXN" },  // Mexican peso (Laredo ↔ Vertiv Mexico lane)
    };

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number when reader.TryGetInt32(out var code) =>
                NumericToAlpha.TryGetValue(code, out var alpha) ? alpha : code.ToString(),
            _ => throw new JsonException(
                $"Unexpected token {reader.TokenType} for AlvysMoney.Currency; expected string, number, or null."),
        };
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}

/// <summary>
/// Pragmatic invoice projection for billing-readiness/worklist support. Unknown JSON
/// properties are tolerated, so this can lag the full Alvys schema. Preserves Alvys
/// PascalCase. Read-only — nothing here is written back to Alvys.
/// </summary>
public sealed class AlvysInvoice
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("Number")]
    public string? Number { get; set; }

    [JsonPropertyName("Type")]
    public string? Type { get; set; }

    [JsonPropertyName("Status")]
    public string? Status { get; set; }

    [JsonPropertyName("CreatedDate")]
    public DateTimeOffset? CreatedDate { get; set; }

    [JsonPropertyName("InvoicedDate")]
    public DateTimeOffset? InvoicedDate { get; set; }

    [JsonPropertyName("DueDate")]
    public DateTimeOffset? DueDate { get; set; }

    [JsonPropertyName("PaidDate")]
    public DateTimeOffset? PaidDate { get; set; }

    [JsonPropertyName("Total")]
    public AlvysMoney? Total { get; set; }

    [JsonPropertyName("AmountPaid")]
    public decimal? AmountPaid { get; set; }

    [JsonPropertyName("RemainingBalance")]
    public decimal? RemainingBalance { get; set; }

    [JsonPropertyName("OverPaymentAmount")]
    public decimal? OverPaymentAmount { get; set; }

    [JsonPropertyName("IsSubmitted")]
    public bool? IsSubmitted { get; set; }

    [JsonPropertyName("LastSendDate")]
    public DateTimeOffset? LastSendDate { get; set; }

    [JsonPropertyName("SupplementalInvoiceType")]
    public string? SupplementalInvoiceType { get; set; }

    [JsonPropertyName("Vendor")]
    public AlvysInvoiceParty? Vendor { get; set; }

    [JsonPropertyName("Customer")]
    public AlvysInvoiceParty? Customer { get; set; }

    [JsonPropertyName("LineItems")]
    public List<AlvysInvoiceLineItem>? LineItems { get; set; }

    [JsonPropertyName("Loads")]
    public List<AlvysInvoiceLoadRef>? Loads { get; set; }

    [JsonPropertyName("Payments")]
    public List<AlvysLoadPayment>? Payments { get; set; }
}

/// <summary>A billing party (vendor or customer) referenced by an invoice.</summary>
public sealed class AlvysInvoiceParty
{
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }
}

/// <summary>A single rate/charge line on an invoice (linehaul/fuel/accessorial/etc.).</summary>
public sealed class AlvysInvoiceLineItem
{
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    [JsonPropertyName("Description")]
    public string? Description { get; set; }

    [JsonPropertyName("Type")]
    public string? Type { get; set; }

    [JsonPropertyName("Quantity")]
    public decimal? Quantity { get; set; }

    [JsonPropertyName("Rate")]
    public decimal? Rate { get; set; }

    [JsonPropertyName("Amount")]
    public decimal? Amount { get; set; }
}

/// <summary>A load referenced by an invoice (links an invoice back to its load).</summary>
public sealed class AlvysInvoiceLoadRef
{
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    [JsonPropertyName("LoadNumber")]
    public string? LoadNumber { get; set; }

    [JsonPropertyName("OrderNumber")]
    public string? OrderNumber { get; set; }
}

/// <summary>
/// Query parameters for the read-only invoice-detail lookup
/// (<c>GET /api/p/v{version}/invoices?id=…|invoiceNumber=…</c>). At least one of the two
/// must be supplied; bound from the internal endpoint query string and passed to
/// <see cref="AlvysApiRoutes.InvoiceDetail"/>. A 404 upstream degrades to <c>null</c>.
/// </summary>
public sealed class InvoiceLookup
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("invoiceNumber")]
    public string? InvoiceNumber { get; set; }

    /// <summary>True when at least one lookup key is supplied (non-blank).</summary>
    [JsonIgnore]
    public bool HasCriteria =>
        !string.IsNullOrWhiteSpace(Id) || !string.IsNullOrWhiteSpace(InvoiceNumber);

    /// <summary>Guards that at least one of <see cref="Id"/>/<see cref="InvoiceNumber"/> is supplied.</summary>
    public void Validate()
    {
        if (!HasCriteria)
            throw new ArgumentException(
                "An invoice lookup requires one of id or invoiceNumber.", nameof(InvoiceLookup));
    }
}

// ---------------------------------------------------------------------------
// Visibility history (read-only): inbound/outbound event timelines by load number.
// ---------------------------------------------------------------------------

/// <summary>
/// A single inbound/outbound visibility event shared for a load
/// (<c>GET /api/p/v{version}/visibility/{inbound|outbound}/{loadNumber}/history</c>).
/// <see cref="Error"/>/<see cref="Reason"/> carry the upstream failure context surfaced as
/// exception signals. Tolerant of missing/nullable fields and preserves Alvys PascalCase.
/// </summary>
public sealed class AlvysVisibilityHistoryEvent
{
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    [JsonPropertyName("ExternalId")]
    public string? ExternalId { get; set; }

    [JsonPropertyName("TripNumber")]
    public string? TripNumber { get; set; }

    [JsonPropertyName("LoadNumber")]
    public string? LoadNumber { get; set; }

    [JsonPropertyName("EventType")]
    public string? EventType { get; set; }

    [JsonPropertyName("SharedAt")]
    public DateTimeOffset? SharedAt { get; set; }

    [JsonPropertyName("Destination")]
    public string? Destination { get; set; }

    [JsonPropertyName("TruckNumber")]
    public string? TruckNumber { get; set; }

    [JsonPropertyName("DriverName")]
    public string? DriverName { get; set; }

    [JsonPropertyName("TrailerNumber")]
    public string? TrailerNumber { get; set; }

    [JsonPropertyName("StopId")]
    public string? StopId { get; set; }

    [JsonPropertyName("LocationId")]
    public string? LocationId { get; set; }

    [JsonPropertyName("SharedBy")]
    public string? SharedBy { get; set; }

    [JsonPropertyName("Reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("Address")]
    public AlvysContextAddress? Address { get; set; }

    [JsonPropertyName("Coordinates")]
    public AlvysCoordinates? Coordinates { get; set; }

    [JsonPropertyName("Status")]
    public string? Status { get; set; }

    [JsonPropertyName("Error")]
    public string? Error { get; set; }
}

// ---------------------------------------------------------------------------
// Equipment events (read-only): truck + trailer event searches.
// ---------------------------------------------------------------------------

/// <summary>
/// Request body for <c>POST /api/p/v{version}/trucks/events/search</c>.
/// <see cref="StartDate"/> and <see cref="TruckIds"/> are required; <see cref="EndDate"/>
/// is optional (open-ended window). <see cref="Validate"/> enforces both required fields.
/// </summary>
public sealed class TruckEventSearchRequest
{
    [JsonPropertyName("StartDate")]
    public DateTimeOffset? StartDate { get; set; }

    [JsonPropertyName("EndDate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? EndDate { get; set; }

    [JsonPropertyName("TruckIds")]
    public List<string> TruckIds { get; set; } = [];

    /// <summary>Guards the Alvys-required fields before the request is sent.</summary>
    public void Validate()
    {
        if (StartDate is null)
            throw new ArgumentException("StartDate is required.", nameof(StartDate));
        if (TruckIds is not { Count: > 0 })
            throw new ArgumentException("At least one truck id is required.", nameof(TruckIds));
    }
}

/// <summary>
/// Request body for <c>POST /api/p/v{version}/trailers/events/search</c>.
/// <see cref="StartDate"/> and <see cref="TrailerIds"/> are required; <see cref="EndDate"/>
/// is optional (open-ended window). <see cref="Validate"/> enforces both required fields.
/// </summary>
public sealed class TrailerEventSearchRequest
{
    [JsonPropertyName("StartDate")]
    public DateTimeOffset? StartDate { get; set; }

    [JsonPropertyName("EndDate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? EndDate { get; set; }

    [JsonPropertyName("TrailerIds")]
    public List<string> TrailerIds { get; set; } = [];

    /// <summary>Guards the Alvys-required fields before the request is sent.</summary>
    public void Validate()
    {
        if (StartDate is null)
            throw new ArgumentException("StartDate is required.", nameof(StartDate));
        if (TrailerIds is not { Count: > 0 })
            throw new ArgumentException("At least one trailer id is required.", nameof(TrailerIds));
    }
}

/// <summary>
/// A truck event (repair/maintenance/availability/other) used to explain match risk when
/// it overlaps a pickup/delivery window. Tolerant of missing fields; never used to fabricate
/// availability when no event data is returned.
/// </summary>
public sealed class AlvysTruckEvent
{
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    [JsonPropertyName("TruckId")]
    public string? TruckId { get; set; }

    [JsonPropertyName("Title")]
    public string? Title { get; set; }

    [JsonPropertyName("EventType")]
    public string? EventType { get; set; }

    [JsonPropertyName("Description")]
    public string? Description { get; set; }

    [JsonPropertyName("StartDate")]
    public DateTimeOffset? StartDate { get; set; }

    [JsonPropertyName("EndDate")]
    public DateTimeOffset? EndDate { get; set; }

    [JsonPropertyName("Address")]
    public AlvysContextAddress? Address { get; set; }

    [JsonPropertyName("CreatedBy")]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("CreatedAt")]
    public DateTimeOffset? CreatedAt { get; set; }
}

/// <summary>
/// A trailer event (repair/maintenance/availability/other) used to explain match risk when
/// it overlaps a pickup/delivery window. Tolerant of missing fields; never used to fabricate
/// availability when no event data is returned.
/// </summary>
public sealed class AlvysTrailerEvent
{
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    [JsonPropertyName("TrailerId")]
    public string? TrailerId { get; set; }

    [JsonPropertyName("Title")]
    public string? Title { get; set; }

    [JsonPropertyName("EventType")]
    public string? EventType { get; set; }

    [JsonPropertyName("Description")]
    public string? Description { get; set; }

    [JsonPropertyName("StartDate")]
    public DateTimeOffset? StartDate { get; set; }

    [JsonPropertyName("EndDate")]
    public DateTimeOffset? EndDate { get; set; }

    [JsonPropertyName("Address")]
    public AlvysContextAddress? Address { get; set; }

    [JsonPropertyName("CreatedBy")]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("CreatedAt")]
    public DateTimeOffset? CreatedAt { get; set; }
}

/// <summary>OAuth2 token response from the Alvys token endpoint.</summary>
internal sealed record AlvysTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("token_type")] string? TokenType);
