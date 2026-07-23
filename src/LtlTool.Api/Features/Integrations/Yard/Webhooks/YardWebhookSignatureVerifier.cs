using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace LtlTool.Api.Features.Integrations.Yard.Webhooks;

/// <summary>Why a Yard webhook signature check passed or failed, for an honest response and audit logging.</summary>
public enum YardWebhookVerifyResult
{
    Valid,

    /// <summary>No signing secret is configured server-side; the receiver fails closed.</summary>
    NotConfigured,

    /// <summary>The <c>X-Yard-Signature</c> header was missing or not in the <c>t=..,v1=..</c> form.</summary>
    MalformedSignature,

    /// <summary>The signed timestamp was missing/unparseable, or older than the tolerance window.</summary>
    StaleTimestamp,

    /// <summary>The computed HMAC did not match the supplied signature.</summary>
    SignatureMismatch,
}

/// <summary>
/// Verifies the <c>X-Yard-Signature</c> header on an inbound Yard webhook. The signature is an
/// HMAC-SHA256 over <c>{timestamp}.{rawBody}</c> keyed by the shared secret, encoded as
/// <c>t={unixSeconds},v1={hexDigest}</c> (Stripe-style, identical to the Alvys receiver). Verification
/// is constant-time and rejects timestamps outside the tolerance window to blunt replay.
/// </summary>
public interface IYardWebhookSignatureVerifier
{
    YardWebhookVerifyResult Verify(string? signatureHeader, string rawBody, DateTimeOffset now);
}

/// <inheritdoc cref="IYardWebhookSignatureVerifier"/>
public sealed class YardWebhookSignatureVerifier(IOptions<YardWebhookOptions> options)
    : IYardWebhookSignatureVerifier
{
    private readonly YardWebhookOptions _options = options.Value;

    public YardWebhookVerifyResult Verify(string? signatureHeader, string rawBody, DateTimeOffset now)
    {
        if (!_options.HasSecret)
            return YardWebhookVerifyResult.NotConfigured;

        if (!TryParse(signatureHeader, out var timestamp, out var providedHex))
            return YardWebhookVerifyResult.MalformedSignature;

        if (!long.TryParse(timestamp, out var unixSeconds))
            return YardWebhookVerifyResult.StaleTimestamp;

        var age = now.ToUnixTimeSeconds() - unixSeconds;
        // Reject events older than tolerance, and future-dated events beyond the same window (clock skew).
        if (age > _options.ToleranceSeconds || age < -_options.ToleranceSeconds)
            return YardWebhookVerifyResult.StaleTimestamp;

        var expected = ComputeHex(_options.Secret, timestamp, rawBody);
        var providedBytes = FromHexOrEmpty(providedHex);
        var expectedBytes = FromHexOrEmpty(expected);

        // Constant-time compare; length mismatch is a mismatch. FixedTimeEquals handles equal lengths.
        if (providedBytes.Length != expectedBytes.Length
            || !CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes))
            return YardWebhookVerifyResult.SignatureMismatch;

        return YardWebhookVerifyResult.Valid;
    }

    /// <summary>Computes the canonical <c>v1</c> hex digest for a timestamp + body under a secret.</summary>
    public static string ComputeHex(string secret, string timestamp, string rawBody)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signed = Encoding.UTF8.GetBytes($"{timestamp}.{rawBody}");
        return Convert.ToHexString(hmac.ComputeHash(signed)).ToLowerInvariant();
    }

    /// <summary>Parses a <c>t=..,v1=..</c> header (order-insensitive, whitespace-tolerant).</summary>
    private static bool TryParse(string? header, out string timestamp, out string signature)
    {
        timestamp = "";
        signature = "";
        if (string.IsNullOrWhiteSpace(header)) return false;

        foreach (var part in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            if (key.Equals("t", StringComparison.OrdinalIgnoreCase)) timestamp = value;
            else if (key.Equals("v1", StringComparison.OrdinalIgnoreCase)) signature = value;
        }

        return timestamp.Length > 0 && signature.Length > 0;
    }

    private static byte[] FromHexOrEmpty(string hex)
    {
        try { return Convert.FromHexString(hex); }
        catch (FormatException) { return []; }
    }
}
