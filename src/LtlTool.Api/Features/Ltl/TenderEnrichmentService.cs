using System.Globalization;
using LtlTool.Api.Features.Integrations.Alvys;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Ltl;

/// <summary>
/// Phase 7.2 "marry the endpoints": Alvys load records frequently lack pallet/piece/weight/volume
/// dimensions, but the inbound EDI <em>tender</em> that created the freight carries them per stop.
/// This service reads tenders (read-only) and, where a tender shares an identifier with a load
/// (<c>ShipmentId</c> / <c>LoadNumber</c> / an order's PO number or reference id), lifts pieces,
/// weight, volume and an estimated pallet count onto the load as an <see cref="LtlEdiEnrichment"/>.
///
/// <para>
/// Honesty rules: the pallet count is always an estimate derived from volume (labelled, with the
/// math shown); weight/volume are copied verbatim from the tender and tagged "EDI tender"; and when
/// no tender matches, nothing is produced — the load's pallet data stays honestly unknown rather
/// than fabricated. Nothing writes back to Alvys.
/// </para>
/// </summary>
public sealed class TenderEnrichmentService(IAlvysClient alvys, IOptions<LtlOptions> options)
{
    private readonly LtlOptions _options = options.Value;

    /// <summary>
    /// Cubic feet occupied by one standard 48"×40" GMA pallet stacked to a typical dry-van door
    /// height — the divisor behind every volume→pallet estimate. A constant equipment assumption,
    /// not Alvys operational data, so the estimate is always clearly an estimate.
    /// </summary>
    private const decimal CubicFeetPerPallet = 96m;

    /// <summary>
    /// Sweeps tenders once (bounded by <see cref="LtlOptions.MaxLoadsScanned"/>) and indexes them by
    /// every identifier a load might join on. Use with <see cref="Enrich"/> on the bulk search path
    /// so a whole page of loads is enriched from a single tender sweep rather than one call per load.
    /// </summary>
    public async Task<TenderEnrichmentIndex> BuildIndexAsync(CancellationToken ct)
    {
        var tenders = new List<AlvysTender>();
        var page = 0;
        var pageSize = _options.AlvysPageSize;

        while (true)
        {
            var response = await alvys.SearchTendersAsync(
                new TenderSearchRequest { Page = page, PageSize = pageSize }, ct);
            if (response.Items.Count == 0) break;

            tenders.AddRange(response.Items);

            if (tenders.Count >= _options.MaxLoadsScanned) break;
            if (tenders.Count >= response.Total || response.Items.Count < pageSize) break;
            page++;
        }

        return TenderEnrichmentIndex.Build(tenders);
    }

    /// <summary>
    /// Detail-path enrichment for a single load: issues targeted tender queries filtered by the
    /// load's own identifiers (so Alvys does the narrowing) and matches against the returned set.
    /// Returns <c>null</c> when no tender shares an identifier — the honest "unknown" case.
    /// </summary>
    public async Task<LtlEdiEnrichment?> EnrichOneAsync(AlvysLoad load, CancellationToken ct)
    {
        var probes = new List<string?> { load.LoadNumber, load.OrderNumber, load.PONumber }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (probes.Count == 0) return null;

        var tenders = new List<AlvysTender>();
        foreach (var probe in probes)
        {
            // Each identifier could be the tender's LoadNumber or its ShipmentId — query both.
            var byLoadNumber = await alvys.SearchTendersAsync(
                new TenderSearchRequest { Page = 0, PageSize = 25, Filter = new TenderSearchFilter { LoadNumber = probe } }, ct);
            tenders.AddRange(byLoadNumber.Items);

            var byShipmentId = await alvys.SearchTendersAsync(
                new TenderSearchRequest { Page = 0, PageSize = 25, Filter = new TenderSearchFilter { ShipmentId = probe } }, ct);
            tenders.AddRange(byShipmentId.Items);
        }

        if (tenders.Count == 0) return null;
        return Enrich(load, TenderEnrichmentIndex.Build(tenders));
    }

    /// <summary>
    /// Matches a load against a prebuilt tender index and, on a hit, computes the enrichment.
    /// Probe order (load LoadNumber → OrderNumber → PO number → reference values) is deterministic
    /// so the same load always resolves to the same tender. Returns <c>null</c> when nothing joins.
    /// </summary>
    public LtlEdiEnrichment? Enrich(AlvysLoad load, TenderEnrichmentIndex index)
    {
        if (index.IsEmpty) return null;

        foreach (var (loadField, value) in EnumerateLoadKeys(load))
        {
            if (index.TryMatch(value, out var tender, out var tenderField))
            {
                return ComputeEnrichment(tender!, $"load {loadField} = tender {tenderField}");
            }
        }

        return null;
    }

    private static IEnumerable<(string Field, string Value)> EnumerateLoadKeys(AlvysLoad load)
    {
        if (!string.IsNullOrWhiteSpace(load.LoadNumber)) yield return ("LoadNumber", load.LoadNumber!);
        if (!string.IsNullOrWhiteSpace(load.OrderNumber)) yield return ("OrderNumber", load.OrderNumber!);
        if (!string.IsNullOrWhiteSpace(load.PONumber)) yield return ("PO number", load.PONumber!);
        foreach (var reference in load.References ?? [])
        {
            if (!string.IsNullOrWhiteSpace(reference.Value)) yield return ("reference", reference.Value!);
        }
    }

    private static LtlEdiEnrichment ComputeEnrichment(AlvysTender tender, string matchedOn)
    {
        var pickup = tender.Stops?.FirstOrDefault(s => string.Equals(s.Type, "Pickup", StringComparison.OrdinalIgnoreCase))
            ?? tender.Stops?.FirstOrDefault();
        var orders = pickup?.Orders ?? [];

        int? pieces = null;
        foreach (var order in orders)
        {
            if (order.Quantity is > 0)
            {
                pieces = (pieces ?? 0) + (int)Math.Round(order.Quantity.Value, MidpointRounding.AwayFromZero);
            }
        }

        var weight = tender.Weight is > 0
            ? tender.Weight
            : SumPositive(orders.Select(o => o.Weight));
        var volume = tender.Volume is > 0
            ? tender.Volume
            : SumPositive(orders.Select(o => o.Volume));

        int? palletEstimate = null;
        string? palletBasis = null;
        if (volume is > 0)
        {
            palletEstimate = (int)Math.Ceiling(volume.Value / CubicFeetPerPallet);
            palletBasis =
                $"{volume.Value.ToString("N0", CultureInfo.InvariantCulture)} cu ft ÷ "
                + $"{CubicFeetPerPallet.ToString("N0", CultureInfo.InvariantCulture)} cu ft/pallet "
                + $"(standard 48×40 pallet) ≈ {palletEstimate} pallets (est.)";
        }

        return new LtlEdiEnrichment
        {
            Source = "EDI tender",
            TenderShipmentId = tender.ShipmentId,
            MatchedOn = matchedOn,
            PieceCount = pieces,
            WeightLbs = weight,
            Volume = volume,
            PalletEstimate = palletEstimate,
            PalletBasis = palletBasis,
        };
    }

    private static decimal? SumPositive(IEnumerable<decimal?> values)
    {
        decimal? total = null;
        foreach (var v in values)
        {
            if (v is > 0) total = (total ?? 0m) + v.Value;
        }
        return total;
    }
}

/// <summary>
/// Immutable index from a tender sweep: maps each joinable identifier (normalized upper/trim) to the
/// tender that carries it and the tender field it came from. First tender wins on a key collision so
/// matching is stable.
/// </summary>
public sealed class TenderEnrichmentIndex
{
    private readonly record struct Hit(AlvysTender Tender, string Field);

    private readonly Dictionary<string, Hit> _byKey;

    private TenderEnrichmentIndex(Dictionary<string, Hit> byKey) => _byKey = byKey;

    public bool IsEmpty => _byKey.Count == 0;

    public static TenderEnrichmentIndex Build(IEnumerable<AlvysTender> tenders)
    {
        var byKey = new Dictionary<string, Hit>(StringComparer.OrdinalIgnoreCase);

        void Add(string? key, AlvysTender tender, string field)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            byKey.TryAdd(key.Trim(), new Hit(tender, field));
        }

        foreach (var tender in tenders)
        {
            Add(tender.ShipmentId, tender, "ShipmentId");
            Add(tender.LoadNumber, tender, "LoadNumber");
            foreach (var stop in tender.Stops ?? [])
            {
                foreach (var order in stop.Orders ?? [])
                {
                    Add(order.PoNumber, tender, "order PO number");
                    Add(order.ReferenceId, tender, "order reference id");
                }
            }
        }

        return new TenderEnrichmentIndex(byKey);
    }

    public bool TryMatch(string value, out AlvysTender? tender, out string? field)
    {
        if (!string.IsNullOrWhiteSpace(value) && _byKey.TryGetValue(value.Trim(), out var hit))
        {
            tender = hit.Tender;
            field = hit.Field;
            return true;
        }
        tender = null;
        field = null;
        return false;
    }
}
