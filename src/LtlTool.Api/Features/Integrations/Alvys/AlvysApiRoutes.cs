namespace LtlTool.Api.Features.Integrations.Alvys;

/// <summary>
/// Builds Alvys public-API request paths under <c>/api/p/v{version}/...</c> from a
/// configured <see cref="AlvysOptions.ApiVersion"/>.
///
/// The <c>v</c> prefix is fixed in the route template, so the configured version is
/// normalized to avoid a double <c>v</c>: both <c>"v2.0"</c> and <c>"2.0"</c> yield
/// the segment <c>v2.0</c>. Paths are returned relative (no leading slash) so they
/// resolve against the host base address of the named API client.
/// </summary>
public static class AlvysApiRoutes
{
    /// <summary>Used when no version is configured.</summary>
    public const string DefaultVersion = "v1";

    public static string LoadsSearch(string? apiVersion) => BuildSearchPath(apiVersion, "loads");

    public static string TripsSearch(string? apiVersion) => BuildSearchPath(apiVersion, "trips");

    public static string TrailersSearch(string? apiVersion) => BuildSearchPath(apiVersion, "trailers");

    public static string TrucksSearch(string? apiVersion) => BuildSearchPath(apiVersion, "trucks");

    public static string DispatchPreferencesSearch(string? apiVersion)
        => BuildSearchPath(apiVersion, "dispatchpreferences");

    public static string LocationsSearch(string? apiVersion) => BuildSearchPath(apiVersion, "locations");

    public static string DriversSearch(string? apiVersion) => BuildSearchPath(apiVersion, "drivers");

    public static string CustomersSearch(string? apiVersion) => BuildSearchPath(apiVersion, "customers");

    public static string UsersSearch(string? apiVersion) => BuildSearchPath(apiVersion, "users");

    public static string TendersSearch(string? apiVersion) => BuildSearchPath(apiVersion, "tenders");

    public static string InvoicesSearch(string? apiVersion) => BuildSearchPath(apiVersion, "invoices");

    public static string TruckEventsSearch(string? apiVersion)
        => BuildSearchPath(apiVersion, "trucks/events");

    public static string TrailerEventsSearch(string? apiVersion)
        => BuildSearchPath(apiVersion, "trailers/events");

    /// <summary>
    /// Relative path <c>api/p/v{version}/invoices?{query}</c> for the read-only invoice-detail
    /// lookup. At least one of <see cref="InvoiceLookup.Id"/>/<see cref="InvoiceLookup.InvoiceNumber"/>
    /// must be supplied (enforced by <see cref="InvoiceLookup.Validate"/>); only the supplied
    /// criteria are emitted and each value is URL-encoded.
    /// </summary>
    public static string InvoiceDetail(string? apiVersion, InvoiceLookup lookup)
    {
        lookup.Validate();
        var query = BuildQuery(
            ("id", lookup.Id),
            ("invoiceNumber", lookup.InvoiceNumber));
        return $"api/p/{NormalizeVersion(apiVersion)}/invoices{query}";
    }

    /// <summary>
    /// Relative path <c>api/p/v{version}/visibility/inbound/{loadNumber}/history</c> for the
    /// read-only inbound visibility-history listing. <paramref name="loadNumber"/> is URL-encoded.
    /// </summary>
    public static string VisibilityInboundHistory(string? apiVersion, string loadNumber)
        => BuildVisibilityHistoryPath(apiVersion, "inbound", loadNumber);

    /// <summary>
    /// Relative path <c>api/p/v{version}/visibility/outbound/{loadNumber}/history</c> for the
    /// read-only outbound visibility-history listing. <paramref name="loadNumber"/> is URL-encoded.
    /// </summary>
    public static string VisibilityOutboundHistory(string? apiVersion, string loadNumber)
        => BuildVisibilityHistoryPath(apiVersion, "outbound", loadNumber);

    /// <summary>
    /// Relative path <c>api/p/v{version}/visibility/{direction}/{loadNumber}/history</c>. The
    /// <paramref name="loadNumber"/> segment is URL-encoded so values with spaces or reserved
    /// characters resolve correctly.
    /// </summary>
    private static string BuildVisibilityHistoryPath(string? apiVersion, string direction, string loadNumber)
        => $"api/p/{NormalizeVersion(apiVersion)}/visibility/{direction}/{Uri.EscapeDataString(loadNumber)}/history";

    /// <summary>
    /// Relative path <c>api/p/v{version}/tenders/{tenderId}</c> for a single tender.
    /// <paramref name="tenderId"/> is URL-encoded so ids with slashes/spaces/reserved
    /// characters resolve to a single path segment.
    /// </summary>
    public static string TenderById(string? apiVersion, string tenderId)
        => $"api/p/{NormalizeVersion(apiVersion)}/tenders/{Uri.EscapeDataString(tenderId)}";

    /// <summary>
    /// Relative path <c>api/p/v{version}/loads/{loadNumber}/documents</c> for the
    /// read-only load-documents listing. <paramref name="loadNumber"/> is URL-encoded.
    /// </summary>
    public static string LoadDocuments(string? apiVersion, string loadNumber)
        => BuildLoadSubresourcePath(apiVersion, loadNumber, "documents");

    /// <summary>
    /// Relative path <c>api/p/v{version}/loads/{loadNumber}/notes</c> for the read-only
    /// load-notes listing. <paramref name="loadNumber"/> is URL-encoded.
    /// </summary>
    public static string LoadNotes(string? apiVersion, string loadNumber)
        => BuildLoadSubresourcePath(apiVersion, loadNumber, "notes");

    /// <summary>
    /// Relative path <c>api/p/v{version}/loads/{loadNumber}/notes</c> for creating a note
    /// (POST). Same path as <see cref="LoadNotes"/>; the verb distinguishes read from write.
    /// </summary>
    public static string CreateLoadNote(string? apiVersion, string loadNumber)
        => BuildLoadSubresourcePath(apiVersion, loadNumber, "notes");

    /// <summary>
    /// Relative path <c>api/p/v{version}/loads/{loadNumber}</c> for a partial update (PATCH)
    /// of a load resource. <paramref name="loadNumber"/> is URL-encoded.
    /// </summary>
    public static string LoadPatch(string? apiVersion, string loadNumber)
        => $"api/p/{NormalizeVersion(apiVersion)}/loads/{Uri.EscapeDataString(loadNumber)}";

    /// <summary>
    /// Relative path <c>api/p/v{version}/trips/{tripId}/assign</c> for assigning a carrier and
    /// assets to a trip (POST). <paramref name="tripId"/> is URL-encoded.
    /// </summary>
    public static string TripAssign(string? apiVersion, string tripId)
        => $"api/p/{NormalizeVersion(apiVersion)}/trips/{Uri.EscapeDataString(tripId)}/assign";

    /// <summary>
    /// Relative path <c>api/p/v{version}/trips/{tripId}/dispatch</c> for dispatching a trip
    /// that already has a carrier and assets assigned (POST). <paramref name="tripId"/> is URL-encoded.
    /// </summary>
    public static string TripDispatch(string? apiVersion, string tripId)
        => $"api/p/{NormalizeVersion(apiVersion)}/trips/{Uri.EscapeDataString(tripId)}/dispatch";

    /// <summary>
    /// Relative path <c>api/p/v{version}/carriers/{carrierId}/status</c> for updating a carrier's
    /// operational status (PATCH). <paramref name="carrierId"/> is URL-encoded.
    /// </summary>
    public static string CarrierStatusPatch(string? apiVersion, string carrierId)
        => $"api/p/{NormalizeVersion(apiVersion)}/carriers/{Uri.EscapeDataString(carrierId)}/status";

    /// <summary>
    /// Relative path <c>api/p/v{version}/tenders/{tenderId}/accept</c> for accepting an inbound
    /// tender. <paramref name="tenderId"/> is URL-encoded.
    /// </summary>
    public static string TenderAccept(string? apiVersion, string tenderId)
        => $"api/p/{NormalizeVersion(apiVersion)}/tenders/{Uri.EscapeDataString(tenderId)}/accept";

    /// <summary>
    /// Relative path <c>api/p/v{version}/trips/{tripId}/stops/{stopId}/arrival</c> for recording
    /// a trip stop arrival (PUT). Both id segments are URL-encoded.
    /// </summary>
    public static string TripStopArrival(string? apiVersion, string tripId, string stopId)
        => $"api/p/{NormalizeVersion(apiVersion)}/trips/{Uri.EscapeDataString(tripId)}/stops/{Uri.EscapeDataString(stopId)}/arrival";

    /// <summary>
    /// Relative path <c>api/p/v{version}/trips/{tripId}/stops/{stopId}/departure</c> for recording
    /// a trip stop departure (PUT). Both id segments are URL-encoded.
    /// </summary>
    public static string TripStopDeparture(string? apiVersion, string tripId, string stopId)
        => $"api/p/{NormalizeVersion(apiVersion)}/trips/{Uri.EscapeDataString(tripId)}/stops/{Uri.EscapeDataString(stopId)}/departure";

    /// <summary>
    /// Relative path <c>api/p/v{version}/loads?{query}</c> for the read-only load-detail
    /// lookup. At least one of <see cref="LoadLookup.Id"/>/<see cref="LoadLookup.LoadNumber"/>/
    /// <see cref="LoadLookup.OrderNumber"/> must be supplied (enforced by
    /// <see cref="LoadLookup.Validate"/>); only the supplied criteria are emitted and each
    /// value is URL-encoded so reserved characters resolve to a single query value.
    /// </summary>
    public static string LoadDetail(string? apiVersion, LoadLookup lookup)
    {
        lookup.Validate();
        var query = BuildQuery(
            ("id", lookup.Id),
            ("loadNumber", lookup.LoadNumber),
            ("orderNumber", lookup.OrderNumber));
        return $"api/p/{NormalizeVersion(apiVersion)}/loads{query}";
    }

    /// <summary>
    /// Relative path <c>api/p/v{version}/trips?{query}</c> for the read-only trip-detail
    /// lookup. At least one of <see cref="TripLookup.Id"/>/<see cref="TripLookup.TripNumber"/>
    /// must be supplied (enforced by <see cref="TripLookup.Validate"/>); the optional
    /// <see cref="TripLookup.IncludeDeleted"/> is emitted as a lowercase <c>true</c>/<c>false</c>
    /// only when set. Values are URL-encoded.
    /// </summary>
    public static string TripDetail(string? apiVersion, TripLookup lookup)
    {
        lookup.Validate();
        var query = BuildQuery(
            ("id", lookup.Id),
            ("tripNumber", lookup.TripNumber),
            ("includeDeleted", lookup.IncludeDeleted?.ToString().ToLowerInvariant()));
        return $"api/p/{NormalizeVersion(apiVersion)}/trips{query}";
    }

    /// <summary>
    /// Relative path <c>api/p/v{version}/trips/{tripId}/stops</c> for the read-only trip-stops
    /// listing. <paramref name="tripId"/> is URL-encoded so ids with slashes/spaces/reserved
    /// characters resolve to a single path segment.
    /// </summary>
    public static string TripStops(string? apiVersion, string tripId)
        => $"api/p/{NormalizeVersion(apiVersion)}/trips/{Uri.EscapeDataString(tripId)}/stops";

    /// <summary>Relative path <c>api/p/v{version}/{resource}/search</c>.</summary>
    public static string BuildSearchPath(string? apiVersion, string resource)
        => $"api/p/{NormalizeVersion(apiVersion)}/{resource}/search";

    /// <summary>
    /// Builds a <c>?key=value&amp;...</c> query string from the supplied pairs, skipping
    /// null/whitespace values and URL-encoding each emitted value. Returns an empty string
    /// (no <c>?</c>) when nothing is emitted.
    /// </summary>
    private static string BuildQuery(params (string Key, string? Value)[] parameters)
    {
        var parts = parameters
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!.Trim())}");
        var query = string.Join("&", parts);
        return query.Length == 0 ? string.Empty : "?" + query;
    }

    /// <summary>
    /// Relative path <c>api/p/v{version}/loads/{loadNumber}/{subresource}</c>. The
    /// <paramref name="loadNumber"/> path segment is URL-encoded so values with spaces or
    /// reserved characters resolve correctly.
    /// </summary>
    public static string BuildLoadSubresourcePath(string? apiVersion, string loadNumber, string subresource)
        => $"api/p/{NormalizeVersion(apiVersion)}/loads/{Uri.EscapeDataString(loadNumber)}/{subresource}";

    /// <summary>
    /// Normalizes a configured version to a single-<c>v</c> segment (e.g. <c>v1</c>,
    /// <c>v2.0</c>). Empty/whitespace falls back to <see cref="DefaultVersion"/>.
    /// </summary>
    public static string NormalizeVersion(string? apiVersion)
    {
        var trimmed = apiVersion?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return DefaultVersion;

        var withoutPrefix = trimmed.TrimStart('v', 'V');
        return string.IsNullOrEmpty(withoutPrefix) ? DefaultVersion : "v" + withoutPrefix;
    }
}
