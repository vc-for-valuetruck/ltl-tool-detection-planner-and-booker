using LtlTool.Api.Features.Integrations.Alvys;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl;

/// <summary>
/// Read-only orchestration for the payroll double-pay guard: fetches the trips for a caller-supplied
/// set of consolidation-group load numbers (the parent and its LTL siblings) via the read-only Alvys
/// trip search, then runs the pure <see cref="PayrollDoublePayAnalyzer"/> over them. Never writes to
/// Alvys, never invents a group — the load-number set comes from the dispatcher/plan the user is
/// looking at, and group membership is confirmed on Alvys' own <c>Main Load Id</c> trip reference.
/// </summary>
public sealed class PayrollDoublePayService(
    IAlvysClient alvys, PayrollDoublePayAnalyzer analyzer, IOptions<LtlOptions> options)
{
    private readonly LtlOptions _options = options.Value;

    /// <summary>
    /// Analyze the trips of the given consolidation-group load numbers for same-driver double-pay.
    /// Returns <see cref="PayrollDoublePayResult.NotEvaluated"/> when no load numbers are supplied or
    /// none of the fetched trips belong to a consolidation group.
    /// </summary>
    public async Task<PayrollDoublePayResult> AnalyzeGroupAsync(
        IReadOnlyList<string> loadNumbers, CancellationToken ct)
    {
        var numbers = (loadNumbers ?? [])
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (numbers.Count == 0) return PayrollDoublePayResult.NotEvaluated;

        var trips = await FetchTripsAsync(numbers, ct);
        return analyzer.Analyze(trips);
    }

    /// <summary>
    /// Fetch every trip for the supplied load numbers (all trips, not deduped by load number — a
    /// group's double-pay evidence lives across sibling trips, so nothing may be collapsed away).
    /// Bounded by the Alvys per-filter cap via chunking, matching the load-service sweep posture.
    /// </summary>
    private async Task<List<AlvysTrip>> FetchTripsAsync(IReadOnlyList<string> loadNumbers, CancellationToken ct)
    {
        var trips = new List<AlvysTrip>();
        var pageSize = Math.Max(1, _options.AlvysPageSize);

        foreach (var chunk in loadNumbers.Chunk(LoadSearchRequest.MaxLoadNumbers))
        {
            var page = 0;
            var fetched = 0;
            while (true)
            {
                var response = await alvys.SearchTripsAsync(
                    new TripSearchRequest { Page = page, PageSize = pageSize, LoadNumbers = [.. chunk] }, ct);
                if (response.Items.Count == 0) break;

                trips.AddRange(response.Items);

                fetched += response.Items.Count;
                if (fetched >= response.Total || response.Items.Count < pageSize) break;
                page++;
            }
        }

        return trips;
    }
}
