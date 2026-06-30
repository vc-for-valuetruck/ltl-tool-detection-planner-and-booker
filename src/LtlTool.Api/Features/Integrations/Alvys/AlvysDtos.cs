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

    [JsonPropertyName("Fleet")]
    public string? Fleet { get; set; }

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

    [JsonPropertyName("IsDeleted")]
    public bool? IsDeleted { get; set; }
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

/// <summary>A typed reference number (e.g. BOL, PO) on a load or stop.</summary>
public sealed class AlvysReference
{
    [JsonPropertyName("Type")]
    public string? Type { get; set; }

    [JsonPropertyName("Value")]
    public string? Value { get; set; }
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

    [JsonPropertyName("TenderAs")]
    public string? TenderAs { get; set; }

    [JsonPropertyName("Stops")]
    public List<AlvysTripStop>? Stops { get; set; }

    [JsonPropertyName("TotalMileage")]
    public decimal? TotalMileage { get; set; }

    [JsonPropertyName("EmptyMileage")]
    public decimal? EmptyMileage { get; set; }

    [JsonPropertyName("LoadedMileage")]
    public decimal? LoadedMileage { get; set; }

    [JsonPropertyName("TripValue")]
    public decimal? TripValue { get; set; }

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

    [JsonPropertyName("Truck")]
    public AlvysEquipmentRef? Truck { get; set; }

    [JsonPropertyName("Trailer")]
    public AlvysTrailer? Trailer { get; set; }

    [JsonPropertyName("Driver")]
    public AlvysPartyPay? Driver { get; set; }

    [JsonPropertyName("Carrier")]
    public AlvysPartyPay? Carrier { get; set; }

    [JsonPropertyName("OwnerOperator")]
    public AlvysPartyPay? OwnerOperator { get; set; }

    [JsonPropertyName("IsDeleted")]
    public bool? IsDeleted { get; set; }
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

    [JsonPropertyName("Accessorials")]
    public List<AlvysAccessorialDetail>? Accessorials { get; set; }

    [JsonPropertyName("EChecks")]
    public List<AlvysECheck>? EChecks { get; set; }
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

/// <summary>OAuth2 token response from the Alvys token endpoint.</summary>
internal sealed record AlvysTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("token_type")] string? TokenType);
