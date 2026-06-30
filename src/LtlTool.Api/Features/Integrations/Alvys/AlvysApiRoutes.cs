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

    /// <summary>Relative path <c>api/p/v{version}/{resource}/search</c>.</summary>
    public static string BuildSearchPath(string? apiVersion, string resource)
        => $"api/p/{NormalizeVersion(apiVersion)}/{resource}/search";

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
