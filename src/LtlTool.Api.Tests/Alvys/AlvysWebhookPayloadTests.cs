using LtlTool.Api.Features.Integrations.Alvys.Webhooks;
using Xunit;

namespace LtlTool.Api.Tests.Alvys;

/// <summary>
/// Verifies the defensive load-number extraction from a webhook body: it reads <c>data.load.LoadNumber</c>
/// when present and returns null (never throws, never invents a value) for any other shape.
/// </summary>
public sealed class AlvysWebhookPayloadTests
{
    [Fact]
    public void Extracts_load_number_from_data_load()
    {
        var body = "{\"eventType\":\"load.changed\",\"data\":{\"load\":{\"LoadNumber\":\"L-2024-777\",\"Status\":\"Delivered\"}}}";
        Assert.Equal("L-2024-777", AlvysWebhookPayload.TryExtractLoadNumber(body));
    }

    [Fact]
    public void Trims_whitespace()
    {
        var body = "{\"data\":{\"load\":{\"LoadNumber\":\"  L-9  \"}}}";
        Assert.Equal("L-9", AlvysWebhookPayload.TryExtractLoadNumber(body));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"data\":{}}")]
    [InlineData("{\"data\":{\"load\":{}}}")]
    [InlineData("{\"data\":{\"load\":{\"LoadNumber\":\"\"}}}")]
    [InlineData("{\"data\":{\"load\":{\"LoadNumber\":123}}}")]
    [InlineData("{\"data\":\"scalar\"}")]
    public void Returns_null_for_missing_or_malformed(string body)
    {
        Assert.Null(AlvysWebhookPayload.TryExtractLoadNumber(body));
    }
}
