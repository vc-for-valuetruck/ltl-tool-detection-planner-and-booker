using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl.Agents;

namespace LtlTool.Api.Tests.Agents;

/// <summary>A <see cref="TimeProvider"/> pinned to a fixed instant for deterministic agent tests.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
    public override DateTimeOffset GetLocalNow() => now;
}

/// <summary>
/// An <see cref="IAlvysClient"/> for the background-agent tests. All read paths delegate to a real
/// <see cref="FallbackAlvysClient"/> (empty results) except the two <c>SearchLoadsAsync</c> overloads,
/// which serve a scripted load list — or return <c>null</c> when <see cref="ReturnNull"/> is set, to
/// exercise the honest 'degraded' probe path. Composition (not subclassing) because
/// <see cref="FallbackAlvysClient"/> is sealed.
/// </summary>
internal sealed class ScriptedAlvysClient : IAlvysClient
{
    private readonly FallbackAlvysClient _inner = new();

    public List<AlvysLoad> Loads { get; init; } = [];

    /// <summary>When true, both SearchLoadsAsync overloads return a null response (contract violation).</summary>
    public bool ReturnNull { get; init; }

    public Task<AlvysLoadsResponse> SearchLoadsAsync(
        int page = 1, int pageSize = 100, string? status = null, CancellationToken ct = default)
    {
        if (ReturnNull) return Task.FromResult<AlvysLoadsResponse>(null!);
        return Task.FromResult(Page(page == 0 ? 0 : page - 1, pageSize));
    }

    public Task<AlvysLoadsResponse> SearchLoadsAsync(LoadSearchRequest request, CancellationToken ct = default)
    {
        if (ReturnNull) return Task.FromResult<AlvysLoadsResponse>(null!);
        return Task.FromResult(Page(request.Page, request.PageSize));
    }

    private AlvysLoadsResponse Page(int page, int pageSize)
    {
        var items = Loads.Skip(page * pageSize).Take(pageSize).ToList();
        return new AlvysLoadsResponse
        {
            Page = page,
            PageSize = pageSize,
            Total = Loads.Count,
            Items = items,
        };
    }

    // Every other read path degrades to the empty fallback — agents never touch these.
    public Task<AlvysLoad?> GetLoadByNumberAsync(string loadNumber, CancellationToken ct = default)
        => _inner.GetLoadByNumberAsync(loadNumber, ct);
    public Task<AlvysLoad?> GetLoadAsync(LoadLookup lookup, CancellationToken ct = default)
        => _inner.GetLoadAsync(lookup, ct);
    public Task<AlvysTrip?> GetTripAsync(TripLookup lookup, CancellationToken ct = default)
        => _inner.GetTripAsync(lookup, ct);
    public Task<IReadOnlyList<AlvysTripStopDetail>> ListTripStopsAsync(string tripId, CancellationToken ct = default)
        => _inner.ListTripStopsAsync(tripId, ct);
    public Task<IReadOnlyList<AlvysLoadDocument>> ListLoadDocumentsAsync(string loadNumber, CancellationToken ct = default)
        => _inner.ListLoadDocumentsAsync(loadNumber, ct);
    public Task<AlvysDocumentContent?> DownloadLoadDocumentAsync(string loadNumber, string documentId, CancellationToken ct = default)
        => _inner.DownloadLoadDocumentAsync(loadNumber, documentId, ct);
    public Task<IReadOnlyList<AlvysLoadNote>> ListLoadNotesAsync(string loadNumber, CancellationToken ct = default)
        => _inner.ListLoadNotesAsync(loadNumber, ct);
    public Task<AlvysTripsResponse> SearchTripsAsync(TripSearchRequest request, CancellationToken ct = default)
        => _inner.SearchTripsAsync(request, ct);
    public Task<AlvysTrailersResponse> SearchTrailersAsync(TrailerSearchRequest request, CancellationToken ct = default)
        => _inner.SearchTrailersAsync(request, ct);
    public Task<AlvysTrucksResponse> SearchTrucksAsync(TruckSearchRequest request, CancellationToken ct = default)
        => _inner.SearchTrucksAsync(request, ct);
    public Task<IReadOnlyList<AlvysDispatchPreference>> SearchDispatchPreferencesAsync(DispatchPreferenceSearchRequest request, CancellationToken ct = default)
        => _inner.SearchDispatchPreferencesAsync(request, ct);
    public Task<AlvysLocationsResponse> SearchLocationsAsync(LocationSearchRequest request, CancellationToken ct = default)
        => _inner.SearchLocationsAsync(request, ct);
    public Task<AlvysDriversResponse> SearchDriversAsync(DriverSearchRequest request, CancellationToken ct = default)
        => _inner.SearchDriversAsync(request, ct);
    public Task<AlvysCustomersResponse> SearchCustomersAsync(CustomerSearchRequest request, CancellationToken ct = default)
        => _inner.SearchCustomersAsync(request, ct);
    public Task<AlvysUsersResponse> SearchUsersAsync(UserSearchRequest request, CancellationToken ct = default)
        => _inner.SearchUsersAsync(request, ct);
    public Task<AlvysTendersResponse> SearchTendersAsync(TenderSearchRequest request, CancellationToken ct = default)
        => _inner.SearchTendersAsync(request, ct);
    public Task<AlvysTender?> GetTenderByIdAsync(string tenderId, CancellationToken ct = default)
        => _inner.GetTenderByIdAsync(tenderId, ct);
    public Task<AlvysInvoicesResponse> SearchInvoicesAsync(InvoiceSearchRequest request, CancellationToken ct = default)
        => _inner.SearchInvoicesAsync(request, ct);
    public Task<AlvysInvoice?> GetInvoiceAsync(InvoiceLookup lookup, CancellationToken ct = default)
        => _inner.GetInvoiceAsync(lookup, ct);
    public Task<IReadOnlyList<AlvysVisibilityHistoryEvent>> ListInboundVisibilityHistoryAsync(string loadNumber, CancellationToken ct = default)
        => _inner.ListInboundVisibilityHistoryAsync(loadNumber, ct);
    public Task<IReadOnlyList<AlvysVisibilityHistoryEvent>> ListOutboundVisibilityHistoryAsync(string loadNumber, CancellationToken ct = default)
        => _inner.ListOutboundVisibilityHistoryAsync(loadNumber, ct);
    public Task<IReadOnlyList<AlvysTruckEvent>> SearchTruckEventsAsync(TruckEventSearchRequest request, CancellationToken ct = default)
        => _inner.SearchTruckEventsAsync(request, ct);
    public Task<IReadOnlyList<AlvysTrailerEvent>> SearchTrailerEventsAsync(TrailerEventSearchRequest request, CancellationToken ct = default)
        => _inner.SearchTrailerEventsAsync(request, ct);
}

/// <summary>
/// In-memory <see cref="IAgentHeartbeatStore"/> that keeps the latest heartbeat per agent so tests can
/// assert the recorded status / error type without a database.
/// </summary>
internal sealed class RecordingAgentHeartbeatStore : IAgentHeartbeatStore
{
    private readonly Dictionary<string, AgentHeartbeat> _latest = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, AgentHeartbeat> Latest => _latest;

    public Task RecordAsync(
        string agentName, DateTimeOffset lastRunAt, string status,
        int? windowSweptCount, string? lastErrorType, CancellationToken ct)
    {
        _latest[agentName] = new AgentHeartbeat
        {
            Id = Guid.NewGuid(),
            AgentName = agentName,
            LastRunAt = lastRunAt,
            Status = status,
            WindowSweptCount = windowSweptCount,
            LastErrorType = lastErrorType,
        };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AgentHeartbeat>> LatestAllAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AgentHeartbeat>>(
            _latest.Values.OrderBy(h => h.AgentName, StringComparer.Ordinal).ToArray());

    public Task<AgentHeartbeat?> LatestAsync(string agentName, CancellationToken ct) =>
        Task.FromResult(_latest.TryGetValue(agentName, out var hb) ? hb : null);
}
