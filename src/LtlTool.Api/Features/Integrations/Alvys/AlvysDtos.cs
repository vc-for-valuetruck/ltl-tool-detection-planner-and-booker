using System.Text.Json.Serialization;

namespace LtlTool.Api.Features.Integrations.Alvys;

/// <summary>
/// Generic Alvys v1 paged envelope: <c>{ Page, PageSize, Total, Items[] }</c>.
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
/// Request body for <c>POST {ApiBaseUrl}/loads/search</c>. Page is 0-based.
/// Alvys requires at least one filter; <see cref="Status"/> defaults to the full
/// status list when no specific status is supplied.
/// </summary>
public sealed class LoadSearchRequest
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

    /// <summary>Full set of Alvys load statuses — used when no status filter is supplied.</summary>
    public static readonly List<string> AllStatuses =
    [
        "Admin", "In Review", "Open", "Quoted", "Reserved", "Covered",
        "Dispatched", "In Transit", "Delivered", "TONU", "Released",
        "Released-Carrier Paid", "Carrier Paid", "Trip Completed", "Queued",
        "Invoiced", "Financed", "Completed", "Paid", "Cancelled", "En-Route",
    ];
}

/// <summary>Loads search response: paged envelope of <see cref="AlvysLoad"/>.</summary>
public sealed class AlvysLoadsResponse : AlvysPagedResponse<AlvysLoad>;

/// <summary>
/// Minimal load projection needed by the client skeleton. Extended in later
/// phases as planner/booker mapping requirements are pinned down.
/// </summary>
public sealed class AlvysLoad
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("LoadNumber")]
    public string? LoadNumber { get; set; }

    [JsonPropertyName("CustomerName")]
    public string? CustomerName { get; set; }

    [JsonPropertyName("Status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("ScheduledPickupAt")]
    public DateTimeOffset? ScheduledPickupAt { get; set; }

    [JsonPropertyName("ScheduledDeliveryAt")]
    public DateTimeOffset? ScheduledDeliveryAt { get; set; }

    [JsonPropertyName("UpdatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>OAuth2 token response from the Alvys token endpoint.</summary>
internal sealed record AlvysTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("token_type")] string? TokenType);
