namespace LtlTool.Api.Features.Integrations.Alvys;

/// <summary>
/// Abstraction over the Alvys TMS API for LTL data.
/// <list type="bullet">
///   <item><description><see cref="AlvysClient"/> — live OAuth2 client; the default source of truth.</description></item>
///   <item><description><see cref="FallbackAlvysClient"/> — empty-result stub for local/UAT fallback only.</description></item>
/// </list>
/// </summary>
public interface IAlvysClient
{
    /// <summary>
    /// Searches loads via <c>POST /loads/search</c>. <paramref name="page"/> is
    /// 1-based and translated to the Alvys 0-based page internally.
    /// </summary>
    Task<AlvysLoadsResponse> SearchLoadsAsync(
        int page = 1, int pageSize = 100, string? status = null, CancellationToken ct = default);

    /// <summary>
    /// Returns a single load by its Alvys load number, or <c>null</c> when not
    /// found or on a transport error.
    /// </summary>
    Task<AlvysLoad?> GetLoadByNumberAsync(string loadNumber, CancellationToken ct = default);
}
