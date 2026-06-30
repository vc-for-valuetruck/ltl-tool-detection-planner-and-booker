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

/// <summary>OAuth2 token response from the Alvys token endpoint.</summary>
internal sealed record AlvysTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("token_type")] string? TokenType);
