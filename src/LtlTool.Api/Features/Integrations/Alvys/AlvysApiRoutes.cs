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

    /// <summary>Relative path <c>api/p/v{version}/{resource}/search</c>.</summary>
    public static string BuildSearchPath(string? apiVersion, string resource)
        => $"api/p/{NormalizeVersion(apiVersion)}/{resource}/search";

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
