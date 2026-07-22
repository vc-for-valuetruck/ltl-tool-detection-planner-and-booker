using System.Text;
using LtlTool.Api.Features.Ltl.Bol;
using LtlTool.Api.Features.Ltl.Dock;
using Xunit;

namespace LtlTool.Api.Tests.Ltl.Bol;

/// <summary>
/// The dependency-free text-layer extractor. It reads back the show-strings written by our own
/// <see cref="SimplePdfDocument"/> (a real, valid PDF), fails closed on non-PDF bytes, and never
/// fabricates text from an image-only document.
/// </summary>
public sealed class BuiltInPdfTextExtractorTests
{
    private readonly BuiltInPdfTextExtractor _extractor = new();

    [Fact]
    public void Name_is_the_honest_builtin_identifier()
        => Assert.Equal("builtin-pdf-textlayer", _extractor.Name);

    [Fact]
    public async Task Null_or_empty_content_yields_null_not_an_empty_string()
    {
        Assert.Null(await _extractor.ExtractTextAsync([], "application/pdf"));
        Assert.Null(await _extractor.ExtractTextAsync(null!, "application/pdf"));
    }

    [Fact]
    public async Task Non_pdf_bytes_yield_null_so_the_caller_can_fail_closed()
    {
        // A JPEG-ish byte run: no %PDF header → not a text-layer PDF we can read.
        var notPdf = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 };

        Assert.Null(await _extractor.ExtractTextAsync(notPdf, "image/jpeg"));
    }

    [Fact]
    public async Task Reads_the_text_layer_of_a_pdf_we_generated()
    {
        var pdf = new SimplePdfDocument()
            .Heading("Combined BOL Packet")
            .Line("Pallet count: 12")
            .Build();

        var text = await _extractor.ExtractTextAsync(pdf, "application/pdf");

        Assert.NotNull(text);
        Assert.Contains("Combined BOL Packet", text);
        Assert.Contains("Pallet count: 12", text);
    }

    [Fact]
    public async Task Produces_a_pdf_that_starts_with_the_header_and_ends_with_eof()
    {
        var pdf = new SimplePdfDocument().Line("anything").Build();
        var raw = Encoding.ASCII.GetString(pdf);

        Assert.StartsWith("%PDF-1.4", raw);
        Assert.EndsWith("%%EOF", raw);
    }
}
