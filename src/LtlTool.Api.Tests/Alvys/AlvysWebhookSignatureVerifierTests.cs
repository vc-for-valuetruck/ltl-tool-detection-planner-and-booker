using LtlTool.Api.Features.Integrations.Alvys.Webhooks;
using Microsoft.Extensions.Options;
using Xunit;

namespace LtlTool.Api.Tests.Alvys;

/// <summary>
/// Verifies the inbound webhook HMAC check: a correctly-signed request is accepted, a tampered body or
/// bad digest is rejected, a stale timestamp is rejected, a malformed header is rejected, and — critically
/// — a receiver with no configured secret fails closed (never accepts an unverified event).
/// </summary>
public sealed class AlvysWebhookSignatureVerifierTests
{
    private const string Secret = "whsec_test_9f8e7d6c5b4a";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_770_000_000);

    private static AlvysWebhookSignatureVerifier Verifier(string secret = Secret, int tolerance = 300) =>
        new(Microsoft.Extensions.Options.Options.Create(new AlvysWebhookOptions { Secret = secret, ToleranceSeconds = tolerance }));

    private static string Header(long unixSeconds, string body, string secret = Secret)
    {
        var digest = AlvysWebhookSignatureVerifier.ComputeHex(secret, unixSeconds.ToString(), body);
        return $"t={unixSeconds},v1={digest}";
    }

    [Fact]
    public void Valid_signature_is_accepted()
    {
        var body = "{\"data\":{\"load\":{\"LoadNumber\":\"L-1001\"}}}";
        var header = Header(Now.ToUnixTimeSeconds(), body);

        Assert.Equal(AlvysWebhookVerifyResult.Valid, Verifier().Verify(header, body, Now));
    }

    [Fact]
    public void Tampered_body_is_a_mismatch()
    {
        var signedBody = "{\"data\":{\"load\":{\"LoadNumber\":\"L-1001\"}}}";
        var header = Header(Now.ToUnixTimeSeconds(), signedBody);

        var tampered = signedBody.Replace("L-1001", "L-9999");
        Assert.Equal(AlvysWebhookVerifyResult.SignatureMismatch, Verifier().Verify(header, tampered, Now));
    }

    [Fact]
    public void Wrong_secret_is_a_mismatch()
    {
        var body = "{\"a\":1}";
        var header = Header(Now.ToUnixTimeSeconds(), body, secret: "whsec_a_different_secret");

        Assert.Equal(AlvysWebhookVerifyResult.SignatureMismatch, Verifier().Verify(header, body, Now));
    }

    [Fact]
    public void Stale_timestamp_is_rejected()
    {
        var body = "{\"a\":1}";
        var signedAt = Now.AddSeconds(-600); // older than the 300s tolerance
        var header = Header(signedAt.ToUnixTimeSeconds(), body);

        Assert.Equal(AlvysWebhookVerifyResult.StaleTimestamp, Verifier().Verify(header, body, Now));
    }

    [Fact]
    public void Future_timestamp_beyond_skew_is_rejected()
    {
        var body = "{\"a\":1}";
        var signedAt = Now.AddSeconds(600);
        var header = Header(signedAt.ToUnixTimeSeconds(), body);

        Assert.Equal(AlvysWebhookVerifyResult.StaleTimestamp, Verifier().Verify(header, body, Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("v1=abc")]      // missing t=
    [InlineData("t=123")]       // missing v1=
    public void Malformed_header_is_rejected(string? header)
    {
        Assert.Equal(AlvysWebhookVerifyResult.MalformedSignature, Verifier().Verify(header, "{}", Now));
    }

    [Fact]
    public void No_configured_secret_fails_closed()
    {
        var body = "{\"a\":1}";
        var header = Header(Now.ToUnixTimeSeconds(), body);

        Assert.Equal(AlvysWebhookVerifyResult.NotConfigured, Verifier(secret: "").Verify(header, body, Now));
    }
}
