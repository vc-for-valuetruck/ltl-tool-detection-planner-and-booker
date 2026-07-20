using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
using Xunit;

namespace LtlTool.Api.Tests.Ltl;

/// <summary>
/// Phase 7.2 load↔tender enrichment. Verifies the join keys (LoadNumber / OrderNumber↔ShipmentId /
/// order references), the honest no-match (null, never fabricated), and the always-"est." pallet
/// math derived from tender volume.
/// </summary>
public class TenderEnrichmentServiceTests
{
    private static TenderEnrichmentService Service(FakeAlvysClient client) =>
        new(client, LtlTestFactory.Options());

    private static AlvysTender Tender(
        string? shipmentId = null,
        string? loadNumber = null,
        decimal? weight = null,
        decimal? volume = null,
        decimal? orderQuantity = null,
        string? orderPo = null,
        string? orderReferenceId = null)
        => new()
        {
            Id = shipmentId ?? loadNumber ?? "T",
            ShipmentId = shipmentId,
            LoadNumber = loadNumber,
            Weight = weight,
            Volume = volume,
            Status = "New",
            Stops =
            [
                new AlvysTenderStop
                {
                    Type = "Pickup",
                    Orders =
                    [
                        new AlvysTenderOrderDetail
                        {
                            Quantity = orderQuantity,
                            PoNumber = orderPo,
                            ReferenceId = orderReferenceId,
                        },
                    ],
                },
            ],
        };

    [Fact]
    public void Enrich_matches_load_ordernumber_to_tender_shipmentid()
    {
        var svc = Service(new FakeAlvysClient());
        var index = TenderEnrichmentIndex.Build(
            [Tender(shipmentId: "98448085", weight: 42359.59m, volume: 1273.489m, orderQuantity: 1843m)]);

        var load = new AlvysLoad { Id = "L1", LoadNumber = "1004400", OrderNumber = "98448085" };
        var enrichment = svc.Enrich(load, index);

        Assert.NotNull(enrichment);
        Assert.Equal("EDI tender", enrichment!.Source);
        Assert.Equal("98448085", enrichment.TenderShipmentId);
        Assert.Equal("load OrderNumber = tender ShipmentId", enrichment.MatchedOn);
        Assert.Equal(1843, enrichment.PieceCount);
        Assert.Equal(42359.59m, enrichment.WeightLbs);
        Assert.Equal(1273.489m, enrichment.Volume);
    }

    [Fact]
    public void Enrich_matches_load_loadnumber_to_tender_loadnumber_first()
    {
        var svc = Service(new FakeAlvysClient());
        var index = TenderEnrichmentIndex.Build([Tender(loadNumber: "1004400", volume: 200m)]);

        var load = new AlvysLoad { Id = "L1", LoadNumber = "1004400" };
        var enrichment = svc.Enrich(load, index);

        Assert.NotNull(enrichment);
        Assert.Equal("load LoadNumber = tender LoadNumber", enrichment!.MatchedOn);
    }

    [Fact]
    public void Enrich_matches_on_order_reference_id()
    {
        var svc = Service(new FakeAlvysClient());
        var index = TenderEnrichmentIndex.Build(
            [Tender(shipmentId: "ZZZ", orderReferenceId: "2630373086", volume: 96m)]);

        var load = new AlvysLoad
        {
            Id = "L1",
            References = [new AlvysReference { Value = "2630373086" }],
        };
        var enrichment = svc.Enrich(load, index);

        Assert.NotNull(enrichment);
        Assert.Equal("load reference = tender order reference id", enrichment!.MatchedOn);
    }

    [Fact]
    public void Enrich_returns_null_when_no_identifier_joins()
    {
        var svc = Service(new FakeAlvysClient());
        var index = TenderEnrichmentIndex.Build([Tender(shipmentId: "98448085", volume: 100m)]);

        var load = new AlvysLoad { Id = "L1", LoadNumber = "1004400", OrderNumber = "M404243763" };
        Assert.Null(svc.Enrich(load, index));
    }

    [Fact]
    public void Enrich_pallet_estimate_ceilings_volume_over_96_and_is_labelled_est()
    {
        var svc = Service(new FakeAlvysClient());
        // 1273.489 / 96 = 13.26… → ceil 14 pallets.
        var index = TenderEnrichmentIndex.Build([Tender(shipmentId: "S1", volume: 1273.489m)]);

        var enrichment = svc.Enrich(new AlvysLoad { Id = "L1", OrderNumber = "S1" }, index);

        Assert.NotNull(enrichment);
        Assert.Equal(14, enrichment!.PalletEstimate);
        Assert.Contains("÷", enrichment.PalletBasis);
        Assert.Contains("(est.)", enrichment.PalletBasis);
    }

    [Fact]
    public void Enrich_no_volume_yields_no_pallet_estimate()
    {
        var svc = Service(new FakeAlvysClient());
        var index = TenderEnrichmentIndex.Build([Tender(shipmentId: "S1", weight: 5000m)]);

        var enrichment = svc.Enrich(new AlvysLoad { Id = "L1", OrderNumber = "S1" }, index);

        Assert.NotNull(enrichment);
        Assert.Null(enrichment!.PalletEstimate);
        Assert.Null(enrichment.PalletBasis);
        Assert.Equal(5000m, enrichment.WeightLbs);
    }

    [Fact]
    public async Task EnrichOneAsync_finds_tender_by_shipmentid_filter()
    {
        var client = new FakeAlvysClient
        {
            Tenders = [Tender(shipmentId: "98448085", volume: 96m, orderQuantity: 10m)],
        };
        var svc = Service(client);

        var load = new AlvysLoad { Id = "L1", LoadNumber = "1004400", OrderNumber = "98448085" };
        var enrichment = await svc.EnrichOneAsync(load, CancellationToken.None);

        Assert.NotNull(enrichment);
        Assert.Equal(10, enrichment!.PieceCount);
        Assert.Equal(1, enrichment.PalletEstimate);
    }

    [Fact]
    public async Task EnrichOneAsync_returns_null_when_no_tender_matches()
    {
        var client = new FakeAlvysClient { Tenders = [Tender(shipmentId: "OTHER", volume: 96m)] };
        var svc = Service(client);

        var load = new AlvysLoad { Id = "L1", LoadNumber = "1004400", OrderNumber = "M404243763" };
        Assert.Null(await svc.EnrichOneAsync(load, CancellationToken.None));
    }
}
