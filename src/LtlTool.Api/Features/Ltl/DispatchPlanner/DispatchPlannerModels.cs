namespace LtlTool.Api.Features.Ltl.DispatchPlanner;

/// <summary>
/// A resolved dispatch-preference pairing for a driver/truck/trailer, projected from the Alvys
/// Public API's <c>dispatchpreferences/search</c>. Honest by construction: when Alvys returns no
/// matching preference (or the read degraded — 429/error), <see cref="Resolved"/> is false and every
/// id is null. Nothing is fabricated; the UI renders "—" for absent fields.
/// </summary>
public sealed class DispatchPreferenceView
{
    /// <summary>True only when Alvys returned at least one matching preference.</summary>
    public bool Resolved { get; init; }

    /// <summary>Preferred dispatcher id, when Alvys carries one.</summary>
    public string? DispatcherId { get; init; }

    /// <summary>Preferred primary driver id.</summary>
    public string? Driver1Id { get; init; }

    /// <summary>Preferred secondary (team) driver id.</summary>
    public string? Driver2Id { get; init; }

    /// <summary>Preferred truck id.</summary>
    public string? TruckId { get; init; }

    /// <summary>Preferred trailer id.</summary>
    public string? TrailerId { get; init; }

    /// <summary>When the winning preference was last updated in Alvys; null when unresolved.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Honest provenance note surfaced in the UI/PR trail.</summary>
    public string Source { get; init; } =
        "Alvys Public API dispatch preferences (read-only). Absent fields shown as — , never inferred.";

    /// <summary>The honest "nothing resolved" view — used on empty result or a degraded read.</summary>
    public static DispatchPreferenceView Unresolved { get; } = new() { Resolved = false };
}

/// <summary>
/// A pragmatic projection of an Alvys location used to enrich yard/warehouse metadata (name, type,
/// physical address) on the yard picker and printed BOL packet. Populated only from a live Alvys
/// <c>locations/search</c> read; when the location cannot be resolved the caller degrades to the
/// static config strings rather than showing a blank.
/// </summary>
public sealed class LocationView
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? Type { get; init; }

    /// <summary>A single-line "Street, City, ST Zip" label, or whichever parts are present.</summary>
    public string? AddressLabel { get; init; }
}
