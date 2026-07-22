using LtlTool.Api.Features.Ltl.Bol;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Bol;

/// <summary>
/// Behavior of the deterministic default field extractor. It never invents values, captures the source
/// line verbatim as evidence, and emits at most one suggestion per field. Missing fields are simply
/// absent (never coerced to 0 / false / "good").
/// </summary>
public sealed class RegexBolFieldExtractorTests
{
    private readonly RegexBolFieldExtractor _extractor = new();

    [Fact]
    public void Name_is_the_honest_deterministic_identifier()
        => Assert.Equal("deterministic-regex", _extractor.Name);

    [Fact]
    public void Empty_or_whitespace_text_yields_no_suggestions()
    {
        Assert.Empty(_extractor.Extract(""));
        Assert.Empty(_extractor.Extract("   \n  \t "));
    }

    [Fact]
    public void Reads_pallet_piece_weight_class_commodity_and_hazmat_with_verbatim_evidence()
    {
        var text = string.Join('\n',
            "Pallet count: 12",
            "Pieces: 340",
            "Total weight 12,480 lbs",
            "Freight class 92.5",
            "Description of goods: Palletized auto parts",
            "HAZMAT UN1203 flammable");

        var fields = _extractor.Extract(text);

        var pallet = Assert.Single(fields, f => f.Field == BolField.PalletCount);
        Assert.Equal("12", pallet.Value);
        Assert.Contains("Pallet count: 12", pallet.EvidenceQuote);

        var piece = Assert.Single(fields, f => f.Field == BolField.PieceCount);
        Assert.Equal("340", piece.Value);

        // Weight keeps value AND unit verbatim — never converted.
        var weight = Assert.Single(fields, f => f.Field == BolField.Weight);
        Assert.Equal("12,480 lbs", weight.Value);

        var freightClass = Assert.Single(fields, f => f.Field == BolField.FreightClass);
        Assert.Equal("92.5", freightClass.Value);

        var commodity = Assert.Single(fields, f => f.Field == BolField.CommodityDescription);
        Assert.Equal("Palletized auto parts", commodity.Value);

        var hazmat = Assert.Single(fields, f => f.Field == BolField.HazmatFlag);
        Assert.Equal("Yes", hazmat.Value);
    }

    [Fact]
    public void Every_evidence_quote_is_a_verbatim_excerpt_of_a_source_line()
    {
        var lines = new[] { "Pallet count: 8", "Gross weight: 4,100 lb" };
        var text = string.Join('\n', lines);

        foreach (var field in _extractor.Extract(text))
            Assert.Contains(lines, l => l.Contains(field.EvidenceQuote.TrimEnd('…')));
    }

    [Fact]
    public void Only_the_first_match_per_field_is_suggested()
    {
        var text = string.Join('\n', "Pallet count: 12", "Pallets: 99");

        var pallets = _extractor.Extract(text).Where(f => f.Field == BolField.PalletCount).ToArray();

        Assert.Single(pallets);
        Assert.Equal("12", pallets[0].Value);
    }

    [Fact]
    public void Text_with_no_recognizable_fields_returns_empty_not_a_fabricated_zero()
        => Assert.Empty(_extractor.Extract("This BOL has nothing structured the extractor recognizes."));

    [Fact]
    public void Suggestions_are_ordered_stably_by_field_enum()
    {
        var text = string.Join('\n', "HAZMAT present", "Pallet count: 3");

        var fields = _extractor.Extract(text);

        Assert.Equal(
            fields.OrderBy(f => f.Field).Select(f => f.Field),
            fields.Select(f => f.Field));
    }
}
