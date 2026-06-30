namespace LtlTool.Api.Features.Integrations.Alvys;

/// <summary>
/// NON-DEFAULT fallback client for local development and UAT only.
///
/// Returns empty results so the app boots and pages render without a live Alvys
/// tenant. It is never the source of truth and must not be selected in
/// production-like configuration — activate it explicitly via
/// <c>Alvys:Provider = Fallback</c>.
/// </summary>
public sealed class FallbackAlvysClient : IAlvysClient
{
    public Task<AlvysLoadsResponse> SearchLoadsAsync(
        int page = 1, int pageSize = 100, string? status = null, CancellationToken ct = default)
        => Task.FromResult(new AlvysLoadsResponse());

    public Task<AlvysLoad?> GetLoadByNumberAsync(string loadNumber, CancellationToken ct = default)
        => Task.FromResult<AlvysLoad?>(null);
}
